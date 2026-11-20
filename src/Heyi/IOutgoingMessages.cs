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

/// <summary>
/// <see cref="IOutgoingMessages"/> for a specific sender, typically the class it's injected into.
/// With an <c>Idempotency-Key</c>, message IDs are counted per sender, and include a short hash of
/// <typeparamref name="TSender"/>'s full name: <c>{key}/{sender}/{type}/{n}</c>.
/// </summary>
/// <remarks>
/// Use it when code running concurrently in the same request, such as ServiceComposer's composition handlers,
/// sends or publishes the same message type: each sender counts its own messages, so the IDs don't depend
/// on which one sends first. Renaming <typeparamref name="TSender"/> changes the IDs, like renaming a message type.
/// </remarks>
/// <typeparam name="TSender">The class sending the messages.</typeparam>
public interface IOutgoingMessages<TSender> : IOutgoingMessages;
