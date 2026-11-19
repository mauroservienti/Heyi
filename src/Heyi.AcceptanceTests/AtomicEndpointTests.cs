using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;
using NServiceBus.TransactionalSession;

namespace Heyi.AcceptanceTests;

[TestFixture]
[Category("Containers")]
public class AtomicEndpointTests
{
    static readonly TimeSpan MessageTimeout = TimeSpan.FromSeconds(30);
    static readonly TimeSpan NoMessageWindow = TimeSpan.FromSeconds(5);

    WebApplication? app;
    HttpClient client = null!;
    string storageDirectory = null!;

    [OneTimeSetUp]
    public async Task Start()
    {
        storageDirectory = Directory.CreateTempSubdirectory("heyi-").FullName;

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();

        var endpointConfiguration = new EndpointConfiguration("Heyi.Orders");
        endpointConfiguration.UseSerialization<SystemJsonSerializer>();
        endpointConfiguration.UseTransport(new LearningTransport
        {
            StorageDirectory = storageDirectory,
            TransportTransactionMode = TransportTransactionMode.ReceiveOnly
        });
        endpointConfiguration.EnableInstallers();
        endpointConfiguration.EnableOutbox();

        var persistence = endpointConfiguration.UsePersistence<SqlPersistence>();
        var dialect = persistence.SqlDialect<SqlDialect.PostgreSql>();
        dialect.JsonBParameterModifier(parameter => ((NpgsqlParameter)parameter).NpgsqlDbType = NpgsqlDbType.Jsonb);
        persistence.ConnectionBuilder(() => new NpgsqlConnection(Database.ConnectionString));
        persistence.EnableTransactionalSession();

        builder.Services.AddNServiceBusEndpoint(endpointConfiguration);

        builder.Services.AddHeyi(options => options.OpenSessionOptions(() => new SqlPersistenceOpenSessionOptions()));
        builder.Services.AddDbContext<OrdersDbContext>(options => options.UseNpgsql(Database.ConnectionString));
        builder.Services.AddHeyiDbContext<OrdersDbContext>();

        app = builder.Build();

        app.MapPost("/orders/{id:guid}", async (Guid id, OrdersDbContext db, IOutgoingMessages messages) =>
        {
            db.Orders.Add(new Order { Id = id });
            await messages.Publish(new OrderPlaced(id));
            return Results.Created($"/orders/{id}", null);
        })
        .WithTransactionalSession();

        app.MapPost("/rejected-orders/{id:guid}", async (Guid id, OrdersDbContext db, IOutgoingMessages messages) =>
        {
            db.Orders.Add(new Order { Id = id });
            await db.SaveChangesAsync();
            await messages.Publish(new OrderPlaced(id));
            return Results.Conflict();
        })
        .WithTransactionalSession();

        app.MapPost("/failing-orders/{id:guid}", async (Guid id, OrdersDbContext db, IOutgoingMessages messages) =>
        {
            db.Orders.Add(new Order { Id = id });
            await db.SaveChangesAsync();
            await messages.Publish(new OrderPlaced(id));
            throw new InvalidOperationException("Something went wrong after storing the order.");
        })
        .WithTransactionalSession();

        await app.StartAsync();
        client = app.GetTestClient();
    }

    [OneTimeTearDown]
    public async Task Stop()
    {
        client?.Dispose();
        if (app is not null)
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
        Directory.Delete(storageDirectory, recursive: true);
    }

    [Test]
    public async Task A_successful_request_stores_the_data_and_dispatches_the_messages()
    {
        var orderId = Guid.NewGuid();

        using var response = await client.PostAsync($"/orders/{orderId}", null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        Assert.That(await OrderExists(orderId), Is.True);
        await OrderPlacedHandler.Received(orderId).Task.WaitAsync(MessageTimeout);
    }

    [Test]
    public async Task A_rejected_request_rolls_back_the_data_and_the_messages()
    {
        var orderId = Guid.NewGuid();

        using var response = await client.PostAsync($"/rejected-orders/{orderId}", null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(await OrderExists(orderId), Is.False);
        Assert.That(await WasReceivedWithin(orderId, NoMessageWindow), Is.False);
    }

    [Test]
    public async Task A_failing_request_rolls_back_the_data_and_the_messages()
    {
        var orderId = Guid.NewGuid();

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.PostAsync($"/failing-orders/{orderId}", null));

        Assert.That(await OrderExists(orderId), Is.False);
        Assert.That(await WasReceivedWithin(orderId, NoMessageWindow), Is.False);
    }

    static async Task<bool> OrderExists(Guid orderId)
    {
        await using var db = Database.NewOrdersDbContext();
        return await db.Orders.AnyAsync(order => order.Id == orderId);
    }

    static async Task<bool> WasReceivedWithin(Guid orderId, TimeSpan window)
    {
        var received = OrderPlacedHandler.Received(orderId).Task;
        return await Task.WhenAny(received, Task.Delay(window)) == received;
    }
}
