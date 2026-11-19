namespace Heyi.Tests;

using Microsoft.EntityFrameworkCore;

sealed class OrdersDbContext(DbContextOptions<OrdersDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();
}

sealed class Order
{
    public int Id { get; set; }
}
