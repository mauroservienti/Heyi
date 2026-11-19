namespace Microsoft.Extensions.DependencyInjection;

using Heyi;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// Registers Heyi services.
/// </summary>
public static class HeyiServiceCollectionExtensions
{
    /// <summary>
    /// Adds Heyi. Configure NServiceBus first: Heyi relies on an endpoint that is already registered.
    /// </summary>
    public static IServiceCollection AddHeyi(this IServiceCollection services, Action<HeyiOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new HeyiOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);
        services.AddScoped<OutgoingMessages>();
        services.AddScoped<IOutgoingMessages>(sp => sp.GetRequiredService<OutgoingMessages>());

        return services;
    }

    /// <summary>
    /// Makes <typeparamref name="TContext"/> join the transactional session in endpoints marked with
    /// <c>WithTransactionalSession()</c>: it uses the session's connection and transaction, and its pending
    /// changes are saved right before the commit. Outside those endpoints, the context works as registered.
    /// Register the context as usual first, for example with <c>AddDbContext</c>.
    /// Requires the NServiceBus SQL persistence.
    /// </summary>
    public static IServiceCollection AddHeyiDbContext<TContext>(this IServiceCollection services)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(ServiceDescriptor.Scoped<IStorageSessionEnlistment, DbContextEnlistment<TContext>>());

        return services;
    }
}
