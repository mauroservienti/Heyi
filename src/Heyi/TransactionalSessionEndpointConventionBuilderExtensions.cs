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
    /// they're checked when the endpoints are built, so they have no per-request cost. ServiceComposer composition handlers
    /// can't be marked: marking any fails when the endpoints are built.
    /// </remarks>
    /// <param name="builder">The endpoint or route group.</param>
    /// <param name="openSessionOptions">Overrides the open session options registered with <c>AddHeyi</c>.</param>
    public static TBuilder WithTransactionalSession<TBuilder>(this TBuilder builder, Func<OpenSessionOptions>? openSessionOptions = null)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Add(endpointBuilder =>
        {
            ThrowIfCompositionHandlersAreMarked(endpointBuilder);

            endpointBuilder.FilterFactories.Add((context, next) =>
            {
                if (IsOptedOut(context.MethodInfo))
                {
                    return next;
                }

                var filter = TransactionalSessionEndpointFilter.Create(context.ApplicationServices, openSessionOptions);
                return invocationContext => filter.InvokeAsync(invocationContext, next);
            });
        });

        return builder;
    }

    // ServiceComposer merges the attributes of all the composition handlers of a route into the endpoint metadata, but
    // the filter factory only sees the composition pipeline, so it can't honor them, and ignoring them would silently open
    // sessions on routes meant to run without one. Heyi doesn't reference ServiceComposer, so its endpoint builder is
    // matched by name.
    static void ThrowIfCompositionHandlersAreMarked(EndpointBuilder endpointBuilder)
    {
        if (endpointBuilder.GetType().FullName != "ServiceComposer.AspNetCore.CompositionEndpointBuilder"
            || !endpointBuilder.Metadata.Any(metadata => metadata is WithTransactionalSessionAttribute or WithoutTransactionalSessionAttribute))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Composition handlers of {endpointBuilder.DisplayName} are marked with [WithTransactionalSession] or [WithoutTransactionalSession], " +
            "which aren't supported on composition handlers: a request runs all the composition handlers of a route in one transactional session, " +
            "and WithTransactionalSession() can't tell them apart. Remove the attributes.");
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
