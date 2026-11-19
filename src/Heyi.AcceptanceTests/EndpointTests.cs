using System.Net;
using System.Net.Http.Json;
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
public class EndpointTests
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

        // The check runs inside the session, and before it, with the same DbContext.
        app.MapPost("/checked-orders", StoreNewOrder).WithTransactionalSession().WithAcceptanceCheck<OrderIsNew>();
        app.MapPost("/prechecked-orders", StoreNewOrder).WithAcceptanceCheck<OrderIsNew>().WithTransactionalSession();

        app.MapEvent<OrderPlaced>("/order-placed-events");

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

    [Test]
    public async Task An_accept_only_endpoint_publishes_the_body()
    {
        var orderId = Guid.NewGuid();

        using var response = await client.PostAsJsonAsync("/order-placed-events", new OrderPlaced(orderId));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        await OrderPlacedHandler.Received(orderId).Task.WaitAsync(MessageTimeout);
    }

    [Test]
    public async Task The_idempotency_key_becomes_the_message_id()
    {
        var orderId = Guid.NewGuid();
        var key = Guid.NewGuid().ToString();

        using var atomicRequest = new HttpRequestMessage(HttpMethod.Post, $"/orders/{orderId}");
        atomicRequest.Headers.Add("Idempotency-Key", $"\"{key}\"");
        using var response = await client.SendAsync(atomicRequest);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        Assert.That(await OrderPlacedHandler.Received(orderId).Task.WaitAsync(MessageTimeout), Is.EqualTo($"{key}/0"));
    }

    [Test]
    public async Task The_idempotency_key_becomes_the_message_id_of_accepted_messages()
    {
        var orderId = Guid.NewGuid();
        var key = Guid.NewGuid().ToString();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/order-placed-events") { Content = JsonContent.Create(new OrderPlaced(orderId)) };
        request.Headers.Add("Idempotency-Key", $"\"{key}\"");
        using var response = await client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        Assert.That(await OrderPlacedHandler.Received(orderId).Task.WaitAsync(MessageTimeout), Is.EqualTo($"{key}/0"));
    }

    static async Task<IResult> StoreNewOrder(NewOrder order, OrdersDbContext db, IOutgoingMessages messages)
    {
        db.Orders.Add(new Order { Id = order.Id });
        await db.SaveChangesAsync();
        await messages.Publish(new OrderPlaced(order.Id));
        return order.RejectAfterStoring ? Results.Conflict() : Results.Created($"/orders/{order.Id}", null);
    }

    [TestCase("/checked-orders")]
    [TestCase("/prechecked-orders")]
    public async Task An_accepted_check_lets_the_atomic_endpoint_store_and_dispatch(string path)
    {
        var orderId = Guid.NewGuid();

        using var response = await client.PostAsJsonAsync(path, new NewOrder(orderId));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        Assert.That(await OrderExists(orderId), Is.True);
        await OrderPlacedHandler.Received(orderId).Task.WaitAsync(MessageTimeout);
    }

    [TestCase("/checked-orders")]
    [TestCase("/prechecked-orders")]
    public async Task A_rejected_check_stops_the_atomic_endpoint(string path)
    {
        var orderId = Guid.NewGuid();
        using var first = await client.PostAsJsonAsync(path, new NewOrder(orderId));
        Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.Created));

        using var second = await client.PostAsJsonAsync(path, new NewOrder(orderId));

        Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity));
    }

    // The check reads through the DbContext first. If the context stayed on its own connection
    // instead of joining the session's transaction, the stored order would survive the rollback.
    [TestCase("/checked-orders")]
    [TestCase("/prechecked-orders")]
    public async Task After_a_check_the_atomic_endpoint_still_rolls_back(string path)
    {
        var orderId = Guid.NewGuid();

        using var response = await client.PostAsJsonAsync(path, new NewOrder(orderId, RejectAfterStoring: true));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
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
