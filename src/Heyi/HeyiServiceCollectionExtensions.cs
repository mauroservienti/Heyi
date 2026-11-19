namespace Microsoft.Extensions.DependencyInjection;

using Heyi;

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
}
