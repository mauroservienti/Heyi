namespace Microsoft.AspNetCore.Builder;

using System.Collections.Concurrent;
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
    /// To leave some out, mark them, minimal API handlers, or ServiceComposer composition handlers, with
    /// <see cref="WithoutTransactionalSessionAttribute"/>: they're checked when the endpoints are built, so they have no per-request cost.
    /// </remarks>
    /// <param name="builder">The endpoint or route group.</param>
    /// <param name="openSessionOptions">Overrides the open session options registered with <c>AddHeyi</c>.</param>
    public static TBuilder WithTransactionalSession<TBuilder>(this TBuilder builder, Func<OpenSessionOptions>? openSessionOptions = null)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Add(endpointBuilder =>
        {
            if (AreCompositionHandlersOptedOut(endpointBuilder))
            {
                return;
            }

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

    static readonly ConcurrentDictionary<Type, PropertyInfo?> componentsMetadataProperties = new();

    // ServiceComposer's endpoint filters see the composition pipeline, not the handlers, so the filter factory's
    // MethodInfo can't tell whether a handler opted out. ServiceComposer's endpoint builder is internal, but exposes
    // each component's attributes as a public ComponentsMetadata property. A request runs all the handlers of a route
    // in one session, or none: the route is opted out only if all its handlers are.
    static bool AreCompositionHandlersOptedOut(EndpointBuilder endpointBuilder)
    {
        var property = componentsMetadataProperties.GetOrAdd(endpointBuilder.GetType(), static type =>
            type.GetProperty("ComponentsMetadata", typeof(IList<(Type ComponentType, IList<object> Metadata)>)));

        if (property?.GetValue(endpointBuilder) is not IList<(Type ComponentType, IList<object> Metadata)> components)
        {
            return false;
        }

        // Endpoint-scoped view model factories only create the view model, before the handlers run: they don't take part.
        var handlers = components.Where(component => !IsViewModelFactory(component.ComponentType)).ToList();
        if (handlers.Count == 0)
        {
            return false;
        }

        var optedOut = handlers.Where(handler => IsOptedOut(handler.Metadata, handler.ComponentType)).Select(handler => handler.ComponentType).ToList();
        if (optedOut.Count == 0)
        {
            return false;
        }

        if (optedOut.Count == handlers.Count)
        {
            return true;
        }

        var optedIn = handlers.Select(handler => handler.ComponentType).Except(optedOut);
        throw new InvalidOperationException(
            $"Some composition handlers of {endpointBuilder.DisplayName} are marked with [WithoutTransactionalSession] " +
            $"({string.Join(", ", optedOut.Select(type => type.Name))}), and others aren't ({string.Join(", ", optedIn.Select(type => type.Name))}). " +
            "A request runs all the handlers of a route in one transactional session, or none: mark all of them, or none.");
    }

    // Heyi doesn't reference ServiceComposer, so the interface is matched by name.
    static bool IsViewModelFactory(Type componentType) =>
        componentType.GetInterfaces().Any(type => type.FullName == "ServiceComposer.AspNetCore.IEndpointScopedViewModelFactory");

    // The closest attribute wins: the handler method's, then its class'. Since ServiceComposer 5.3.1 and 4.3.3, a component's
    // metadata is its class' attributes followed by its method's, also for contract-less handlers, so it's read from the end.
    // Earlier versions only have the method's attributes, so the class' are read from the component type too.
    static bool IsOptedOut(IList<object> metadata, Type componentType)
    {
        foreach (var attributes in new[] { metadata.Reverse(), componentType.GetCustomAttributes(inherit: true) })
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
