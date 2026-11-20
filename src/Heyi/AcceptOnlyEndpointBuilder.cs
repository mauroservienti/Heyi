namespace Heyi;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

/// <summary>
/// An accept-only endpoint, as returned by <c>MapCommand</c> and <c>MapEvent</c>. Endpoint conventions such as
/// <c>RequireAuthorization</c>, <c>WithName</c>, or <c>ProducesProblem</c> apply as usual, and acceptance checks can be added.
/// </summary>
/// <typeparam name="TMessage">The message the endpoint accepts.</typeparam>
public sealed class AcceptOnlyEndpointBuilder<TMessage> : IEndpointConventionBuilder
    where TMessage : class
{
    readonly RouteHandlerBuilder builder;

    internal AcceptOnlyEndpointBuilder(RouteHandlerBuilder builder) => this.builder = builder;

    /// <inheritdoc />
    public void Add(Action<EndpointBuilder> convention) => builder.Add(convention);

    /// <inheritdoc />
    public void Finally(Action<EndpointBuilder> finallyConvention) => builder.Finally(finallyConvention);

    /// <summary>
    /// Runs <typeparamref name="TCheck"/> before the message is dispatched. When it rejects the message,
    /// its response is returned and nothing is dispatched. Checks run in the order they're added.
    /// </summary>
    /// <remarks>
    /// The check is resolved from the request services if registered,
    /// or created with its constructor dependencies resolved from them.
    /// </remarks>
    /// <typeparam name="TCheck">The check to run.</typeparam>
    public AcceptOnlyEndpointBuilder<TMessage> WithAcceptanceCheck<TCheck>()
        where TCheck : IAcceptanceCheck<TMessage>
    {
        builder.AddEndpointFilter(new AcceptanceCheckEndpointFilter<TCheck, TMessage>());
        return this;
    }
}
