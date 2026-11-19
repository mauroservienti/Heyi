using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Testcontainers.PostgreSql;

namespace Heyi.AcceptanceTests;

[SetUpFixture]
public class Database
{
    static PostgreSqlContainer? container;

    public static string ConnectionString => container?.GetConnectionString()
        ?? throw new InvalidOperationException("The database container isn't running.");

    [OneTimeSetUp]
    public async Task Start()
    {
        container = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await container.StartAsync();

        await using var db = NewOrdersDbContext();
        await db.GetService<IRelationalDatabaseCreator>().CreateTablesAsync();
    }

    [OneTimeTearDown]
    public async Task Stop()
    {
        if (container is not null)
        {
            await container.DisposeAsync();
        }
    }

    public static OrdersDbContext NewOrdersDbContext() =>
        new(new DbContextOptionsBuilder<OrdersDbContext>().UseNpgsql(ConnectionString).Options);
}
