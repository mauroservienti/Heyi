namespace Heyi.Tests;

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

public class DbContextEnlistmentTests
{
    string databasePath = null!;
    string connectionString = null!;

    [SetUp]
    public async Task CreateDatabase()
    {
        databasePath = Path.Combine(Path.GetTempPath(), $"heyi-{Guid.NewGuid():N}.db");
        connectionString = $"Data Source={databasePath};Pooling=False";

        await using var context = CreateContext();
        await context.Database.EnsureCreatedAsync();
    }

    [TearDown]
    public void DeleteDatabase()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(databasePath);
    }

    [Test]
    public async Task Changes_are_saved_with_the_commit()
    {
        await using var app = await StartApp(endpoints =>
            endpoints.MapPost("/orders", (PlaceOrder request, OrdersDbContext db) =>
            {
                db.Orders.Add(new Order { Id = request.OrderId });
                return Results.Created($"/orders/{request.OrderId}", null);
            })
            .WithTransactionalSession());

        var response = await app.CreateClient().PostAsJsonAsync("/orders", new PlaceOrder(42));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        Assert.That(await StoredOrderIds(), Is.EqualTo(new[] { 42 }));
    }

    [Test]
    public async Task Changes_saved_by_the_endpoint_roll_back_with_the_session()
    {
        await using var app = await StartApp(endpoints =>
            endpoints.MapPost("/orders", async (PlaceOrder request, OrdersDbContext db) =>
            {
                db.Orders.Add(new Order { Id = request.OrderId });
                await db.SaveChangesAsync();
                return Results.Conflict();
            })
            .WithTransactionalSession());

        var response = await app.CreateClient().PostAsJsonAsync("/orders", new PlaceOrder(42));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(await StoredOrderIds(), Is.Empty);
    }

    [Test]
    public async Task Context_resolved_before_the_session_opens_still_joins_it()
    {
        // Endpoint parameters are resolved before the filter opens the session.
        System.Data.Common.DbConnection? connectionUsedByTheEndpoint = null;
        await using var app = await StartApp(endpoints =>
            endpoints.MapPost("/orders", (PlaceOrder request, OrdersDbContext db) =>
            {
                connectionUsedByTheEndpoint = db.Database.GetDbConnection();
                db.Orders.Add(new Order { Id = request.OrderId });
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
            })
            .WithTransactionalSession());

        await app.CreateClient().PostAsJsonAsync("/orders", new PlaceOrder(42));

        Assert.That(connectionUsedByTheEndpoint, Is.SameAs(app.Session.StorageSession!.Connection));
        Assert.That(await StoredOrderIds(), Is.Empty);
    }

    [Test]
    public async Task Context_outside_a_transactional_endpoint_uses_its_own_connection()
    {
        await using var app = await StartApp(endpoints =>
            endpoints.MapPost("/orders", async (PlaceOrder request, OrdersDbContext db) =>
            {
                db.Orders.Add(new Order { Id = request.OrderId });
                await db.SaveChangesAsync();
                return Results.Created($"/orders/{request.OrderId}", null);
            }));

        await app.CreateClient().PostAsJsonAsync("/orders", new PlaceOrder(42));

        Assert.That(app.Session.OpenedWith, Is.Null);
        Assert.That(await StoredOrderIds(), Is.EqualTo(new[] { 42 }));
    }

    [Test]
    public async Task Other_persistences_fail_with_a_clear_message()
    {
        await using var app = await TestApp.Start(
            endpoints => endpoints.MapPost("/orders", (OrdersDbContext db) => Results.Ok()).WithTransactionalSession(),
            configureServices: services => services
                .AddDbContext<OrdersDbContext>(options => options.UseSqlite(connectionString))
                .AddHeyiDbContext<OrdersDbContext>());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => app.CreateClient().PostAsync("/orders", null));

        Assert.That(exception!.Message, Does.Contain("SQL persistence"));
    }

    async Task<TestApp> StartApp(Action<Microsoft.AspNetCore.Routing.IEndpointRouteBuilder> map)
    {
        var app = await TestApp.Start(map, configureServices: services => services
            .AddDbContext<OrdersDbContext>(options => options.UseSqlite(connectionString))
            .AddHeyiDbContext<OrdersDbContext>());
        app.Session.StorageSessionFactory = () => new FakeSqlStorageSession(connectionString);
        return app;
    }

    OrdersDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<OrdersDbContext>().UseSqlite(connectionString).Options);

    async Task<int[]> StoredOrderIds()
    {
        await using var context = CreateContext();
        return await context.Orders.Select(order => order.Id).OrderBy(id => id).ToArrayAsync();
    }
}
