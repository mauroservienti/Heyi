namespace Microsoft.AspNetCore.Builder;

using Heyi;
using Microsoft.AspNetCore.Http;

/// <summary>
/// Adds acceptance checks to accept-only endpoints.
/// </summary>
public static class AcceptanceCheckRouteHandlerBuilderExtensions
{
    /// <summary>
    /// Runs <typeparamref name="TCheck"/> before the message is dispatched. When it rejects the message,
    /// its response is returned and nothing is dispatched. Checks run in the order they're added.
    /// </summary>
    /// <remarks>
    /// <typeparamref name="TCheck"/> must implement <see cref="IAcceptanceCheck{TMessage}"/> for the endpoint's message type;
    /// otherwise, building the endpoint fails. The check is resolved from the request services if registered,
    /// or created with its constructor dependencies resolved from them.
    /// </remarks>
    /// <param name="builder">The accept-only endpoint, as returned by <c>MapCommand</c> or <c>MapEvent</c>.</param>
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
                "but the endpoint doesn't accept any of them. Add the check to the MapCommand or MapEvent of the message it checks.");
        });

        return builder;
    }
}
