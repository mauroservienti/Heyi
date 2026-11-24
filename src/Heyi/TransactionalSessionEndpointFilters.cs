namespace Heyi;

using System.ComponentModel;
using Microsoft.AspNetCore.Builder;
using NServiceBus.TransactionalSession;

/// <summary>
/// For integrations that decide, endpoint by endpoint, which endpoints run in a transactional session, such as Heyi.ServiceComposer.
/// Applications use <c>WithTransactionalSession()</c> instead.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class TransactionalSessionEndpointFilters
{
    /// <summary>
    /// Adds the transactional session filter to the endpoint, from an endpoint convention: what <c>WithTransactionalSession()</c>
    /// adds to each endpoint, without its checks on ServiceComposer composition endpoints.
    /// </summary>
    /// <param name="endpointBuilder">The endpoint, from an endpoint convention.</param>
    /// <param name="openSessionOptions">Overrides the open session options registered with <c>AddHeyi</c>.</param>
    public static void Add(EndpointBuilder endpointBuilder, Func<OpenSessionOptions>? openSessionOptions = null)
    {
        ArgumentNullException.ThrowIfNull(endpointBuilder);
        TransactionalSessionEndpointConventionBuilderExtensions.AddFilter(endpointBuilder, openSessionOptions);
    }
}
