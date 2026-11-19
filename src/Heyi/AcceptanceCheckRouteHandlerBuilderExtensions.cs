namespace Microsoft.AspNetCore.Builder;

using Heyi;
using Microsoft.AspNetCore.Http;

/// <summary>
/// Adds acceptance checks to endpoints.
/// </summary>
public static class AcceptanceCheckRouteHandlerBuilderExtensions
{
    /// <summary>
    /// Runs <typeparamref name="TCheck"/> before the endpoint handles the message. When it rejects the message,
    /// its response is returned and the endpoint doesn't run. Endpoint filters run in the order they're added:
    /// on a transactional endpoint, add the check after <c>WithTransactionalSession</c> to run it inside the session.
    /// </summary>
    /// <remarks>
    /// <typeparamref name="TCheck"/> must implement <see cref="IAcceptanceCheck{TMessage}"/> for the type of one of
    /// the endpoint's parameters; otherwise, building the endpoint fails. The check is resolved from the request services if registered,
    /// or created with its constructor dependencies resolved from them.
    /// </remarks>
    /// <param name="builder">The endpoint, for example as returned by <c>MapCommand</c>, <c>MapEvent</c>, or <c>MapPost</c>.</param>
    public static RouteHandlerBuilder WithAcceptanceCheck<TCheck>(this RouteHandlerBuilder builder)
        where TCheck : class
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddEndpointFilterFactory((context, next) =>
        {
            var parameters = context.MethodInfo.GetParameters();
            var messageTypes = typeof(TCheck).GetInterfaces()
                .Where(type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IAcceptanceCheck<>))
                .Select(type => type.GetGenericArguments()[0])
                .ToArray();

            if (messageTypes.Length == 0)
            {
                throw new InvalidOperationException(
                    $"{typeof(TCheck).FullName} doesn't implement IAcceptanceCheck<TMessage>, so it can't be used as an acceptance check.");
            }

            foreach (var messageType in messageTypes)
            {
                var messageIndex = Array.FindIndex(parameters, parameter => parameter.ParameterType == messageType);
                if (messageIndex >= 0)
                {
                    var filterType = typeof(AcceptanceCheckEndpointFilter<,>).MakeGenericType(typeof(TCheck), messageType);
                    var filter = (IEndpointFilter)Activator.CreateInstance(filterType, messageIndex)!;
                    return invocationContext => filter.InvokeAsync(invocationContext, next);
                }
            }

            throw new InvalidOperationException(
                $"{typeof(TCheck).FullName} checks {string.Join(", ", messageTypes.Select(type => type.FullName))}, " +
                "but the endpoint doesn't accept any of them. Add the check to an endpoint with a parameter of the type it checks.");
        });

        return builder;
    }
}
