namespace Microsoft.AspNetCore.Builder;

using Heyi;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NServiceBus.TransactionalSession;
using ServiceComposer.AspNetCore;

/// <summary>
/// Marks ServiceComposer composition endpoints as transactional.
/// </summary>
public static class CompositionEndpointConventionBuilderExtensions
{
    /// <summary>
    /// Wraps each composition endpoint in an NServiceBus transactional session: all the composition handlers of a request
    /// send through the same session, and commit or roll back together.
    /// Safe methods run without a session, unless <c>OpenSessionsForSafeMethods()</c> is set on the Heyi options.
    /// </summary>
    /// <remarks>
    /// A route runs without a session if all its composition handlers and route-scoped subscribers are marked with
    /// <see cref="WithoutTransactionalSessionAttribute"/>, on their class or method. The closest attribute wins:
    /// <see cref="WithTransactionalSessionAttribute"/> on a method opts it back in. A request runs all the handlers of
    /// a route in one session, or none, so marking only some of them fails when the endpoints are built, unless the route
    /// runs without a session anyway, for safe methods. Endpoint-scoped view model factories don't count, and the
    /// attributes are ignored on <c>ICompositionEventsHandler&lt;T&gt;</c> handlers, which run in the session of the
    /// request raising the event.
    /// </remarks>
    /// <param name="builder">The composition endpoints, returned by <c>MapCompositionHandlers()</c>.</param>
    /// <param name="openSessionOptions">Overrides the open session options registered with <c>AddHeyi</c>.</param>
    public static CompositionEndpointConventionBuilder WithTransactionalSession(this CompositionEndpointConventionBuilder builder, Func<OpenSessionOptions>? openSessionOptions = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Add(endpointBuilder =>
        {
            if (endpointBuilder.Metadata.OfType<CompositionEndpointComponents>().FirstOrDefault() is { } components
                && AreHandlersOptedOut(endpointBuilder, components))
            {
                return;
            }

            TransactionalSessionEndpointFilters.Add(endpointBuilder, openSessionOptions);
        });

        return builder;
    }

    static bool AreHandlersOptedOut(EndpointBuilder endpointBuilder, CompositionEndpointComponents components)
    {
        // Endpoint-scoped view model factories only create the view model, before the handlers run: they don't take part.
        var handlers = components.Components.Where(component => component.Kind != CompositionComponentKind.ViewModelFactory).ToList();
        var optedOut = handlers.Where(IsOptedOut).ToList();
        if (optedOut.Count == 0)
        {
            return false;
        }

        if (optedOut.Count == handlers.Count)
        {
            return true;
        }

        // Without a session either way, a partial opt-out changes nothing.
        if (RunsWithoutSession(endpointBuilder))
        {
            return false;
        }

        var optedIn = handlers.Except(optedOut);
        throw new InvalidOperationException(
            $"Some composition handlers of {endpointBuilder.DisplayName} are marked with [WithoutTransactionalSession] " +
            $"({Names(optedOut)}), and others aren't ({Names(optedIn)}). " +
            "A request runs all the handlers of a route in one transactional session, or none: mark all of them, or none.");
    }

    // The closest attribute wins: the method's, then its class'.
    static bool IsOptedOut(CompositionComponent component)
    {
        foreach (var attributes in new[] { component.MethodAttributes, component.TypeAttributes })
        {
            foreach (var attribute in attributes)
            {
                switch (attribute)
                {
                    case WithoutTransactionalSessionAttribute:
                        return true;
                    case WithTransactionalSessionAttribute:
                        return false;
                }
            }
        }

        return false;
    }

    static bool RunsWithoutSession(EndpointBuilder endpointBuilder)
    {
        var httpMethods = endpointBuilder.Metadata.OfType<IHttpMethodMetadata>().FirstOrDefault()?.HttpMethods;
        if (httpMethods is not { Count: > 0 } || !httpMethods.All(IsSafeMethod))
        {
            return false;
        }

        // Without Heyi's options, the transactional session filter fails when it's created, with a clearer error.
        var options = endpointBuilder.ApplicationServices.GetService<HeyiOptions>();
        return options is { SafeMethodsUseSessions: false };
    }

    static bool IsSafeMethod(string method) =>
        HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method);

    static string Names(IEnumerable<CompositionComponent> components) =>
        string.Join(", ", components.Select(component => component.UserType.Name));
}
