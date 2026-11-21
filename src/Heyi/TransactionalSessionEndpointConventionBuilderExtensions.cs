namespace Microsoft.AspNetCore.Builder;

using System.Reflection;
using Heyi;
using Microsoft.AspNetCore.Http;
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
    /// <remarks>
    /// To wrap only some controllers or actions, mark them with <see cref="WithTransactionalSessionAttribute"/> instead.
    /// To leave some out, mark them, or minimal API handlers, with <see cref="WithoutTransactionalSessionAttribute"/>:
    /// they're checked when the endpoints are built, so they have no per-request cost.
    /// </remarks>
    /// <param name="builder">The endpoint or route group.</param>
    /// <param name="openSessionOptions">Overrides the open session options registered with <c>AddHeyi</c>.</param>
    public static TBuilder WithTransactionalSession<TBuilder>(this TBuilder builder, Func<OpenSessionOptions>? openSessionOptions = null)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddEndpointFilterFactory((context, next) =>
        {
            if (IsOptedOut(context.MethodInfo))
            {
                return next;
            }

            var filter = TransactionalSessionEndpointFilter.Create(context.ApplicationServices, openSessionOptions);
            return invocationContext => filter.InvokeAsync(invocationContext, next);
        });

        return builder;
    }

    // The closest attribute wins: the method's, then its controller's. For controllers, ReflectedType is the controller,
    // also for actions inherited from a base class.
    static bool IsOptedOut(MethodInfo method)
    {
        foreach (var target in new MemberInfo?[] { method, method.ReflectedType ?? method.DeclaringType })
        {
            if (target is null)
            {
                continue;
            }

            if (target.IsDefined(typeof(WithoutTransactionalSessionAttribute), inherit: true))
            {
                return true;
            }

            if (target.IsDefined(typeof(WithTransactionalSessionAttribute), inherit: true))
            {
                return false;
            }
        }

        return false;
    }
}
