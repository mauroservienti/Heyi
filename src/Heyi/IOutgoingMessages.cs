namespace Heyi;

using NServiceBus;

/// <summary>
/// Sends and publishes messages through the transactional session of an endpoint marked with
/// <c>WithTransactionalSession()</c>. The messages are committed atomically with the endpoint's data,
/// and only when the endpoint returns a success status code.
/// </summary>
public interface IOutgoingMessages
{
    /// <summary>
    /// Sends a message, routed by the NServiceBus routing configuration unless the options say otherwise.
    /// </summary>
    Task Send(object message, SendOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a message to this endpoint.
    /// </summary>
    Task SendLocal(object message, SendOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes an event.
    /// </summary>
    Task Publish(object message, PublishOptions? options = null, CancellationToken cancellationToken = default);
}
