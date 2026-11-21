namespace Microsoft.AspNetCore.Builder;

using Heyi;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NServiceBus.TransactionalSession;

/// <summary>
/// Marks endpoints and route groups as transactional.
/// </summary>
public static class TransactionalSessionEndpointConventionBuilderExtensions
{
    /// <summary>
    /// Wraps the endpoint in an NServiceBus transactional session: the session is opened before the endpoint runs,
    /// and committed after it returns a success status code and before the response is written.
    /// Exceptions and non-success status codes roll back data and messages.
    /// Safe methods run without a session, unless <c>OpenSessionsForSafeMethods()</c> is set on the Heyi options.
    /// </summary>
    /// <param name="builder">The endpoint or route group.</param>
    /// <param name="openSessionOptions">Overrides the open session options registered with <c>AddHeyi</c>.</param>
    public static TBuilder WithTransactionalSession<TBuilder>(this TBuilder builder, Func<OpenSessionOptions>? openSessionOptions = null)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        return AddTransactionalSession(builder, openSessionOptions, _ => true);
    }

    /// <summary>
    /// Wraps the endpoints marked with <see cref="TransactionalSessionAttribute"/> in an NServiceBus transactional session,
    /// as <c>WithTransactionalSession()</c> does, and leaves the others alone. For example,
    /// <c>app.MapControllers().WithTransactionalSessionAttributes()</c> wraps only the marked controllers and actions.
    /// </summary>
    /// <remarks>
    /// The attribute is checked once, when the endpoints are built, so unmarked endpoints have no per-request cost.
    /// </remarks>
    /// <param name="builder">The endpoints, for example the result of <c>MapControllers()</c> or a route group.</param>
    /// <param name="openSessionOptions">Overrides the open session options registered with <c>AddHeyi</c>.</param>
    public static TBuilder WithTransactionalSessionAttributes<TBuilder>(this TBuilder builder, Func<OpenSessionOptions>? openSessionOptions = null)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        return AddTransactionalSession(builder, openSessionOptions, context => IsMarked(context.MethodInfo));
    }

    static TBuilder AddTransactionalSession<TBuilder>(TBuilder builder, Func<OpenSessionOptions>? openSessionOptions, Func<EndpointFilterFactoryContext, bool> appliesTo)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.AddEndpointFilterFactory((context, next) =>
        {
            if (!appliesTo(context))
            {
                return next;
            }

            var services = context.ApplicationServices;

            var options = services.GetService<HeyiOptions>()
                ?? throw new InvalidOperationException(
                    "Heyi isn't registered. Call services.AddHeyi() after configuring the NServiceBus endpoint.");

            var isService = services.GetRequiredService<IServiceProviderIsService>();
            if (!isService.IsService(typeof(ITransactionalSession)))
            {
                throw new InvalidOperationException(
                    "No ITransactionalSession is registered. Enable the transactional session on the NServiceBus persistence " +
                    "(persistence.EnableTransactionalSession()), enable the outbox, and register the endpoint with AddNServiceBusEndpoint before mapping transactional endpoints.");
            }

            var factory = openSessionOptions ?? options.OpenSessionOptionsFactory
                ?? throw new InvalidOperationException(
                    "No open session options are configured. Use services.AddHeyi(options => options.OpenSessionOptions(() => new ...)) " +
                    "or pass them to WithTransactionalSession(() => new ...).");

            var filter = new TransactionalSessionEndpointFilter(
                factory,
                options.SafeMethodsUseSessions,
                services.GetRequiredService<ILogger<TransactionalSessionEndpointFilter>>());

            return invocationContext => filter.InvokeAsync(invocationContext, next);
        });

        return builder;
    }

    // For controllers, ReflectedType is the controller, also for actions inherited from a base class.
    static bool IsMarked(System.Reflection.MethodInfo method) =>
        method.IsDefined(typeof(TransactionalSessionAttribute), inherit: true)
        || (method.ReflectedType ?? method.DeclaringType)?.IsDefined(typeof(TransactionalSessionAttribute), inherit: true) == true;
}
