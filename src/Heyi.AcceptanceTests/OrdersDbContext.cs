using Microsoft.EntityFrameworkCore;

namespace Heyi.AcceptanceTests;

public class OrdersDbContext(DbContextOptions<OrdersDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();
}

public class Order
{
    public Guid Id { get; set; }
}
