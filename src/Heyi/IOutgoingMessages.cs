namespace Heyi;

using NServiceBus;

/// <summary>
/// The messages an endpoint marked with <c>WithTransactionalSession()</c> sends or publishes.
/// Messages are handed to the transactional session only when the endpoint returns a success
/// status code, and are committed atomically with the endpoint's data.
/// </summary>
public interface IOutgoingMessages
{
    /// <summary>
    /// Sends a message, routed by the NServiceBus routing configuration unless the options say otherwise.
    /// </summary>
    void Send(object message, SendOptions? options = null);

    /// <summary>
    /// Sends a message to this endpoint.
    /// </summary>
    void SendLocal(object message, SendOptions? options = null);

    /// <summary>
    /// Publishes an event.
    /// </summary>
    void Publish(object message, PublishOptions? options = null);
}
