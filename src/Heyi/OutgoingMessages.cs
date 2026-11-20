namespace Heyi;

using NServiceBus;
using NServiceBus.TransactionalSession;

sealed class OutgoingMessages(ITransactionalSession session) : IOutgoingMessages
{
    bool active;
    IdempotencyKey? idempotencyKey;

    internal void Activate(IdempotencyKey? key)
    {
        active = true;
        idempotencyKey = key;
    }

    public Task Send(object message, SendOptions? options = null, CancellationToken cancellationToken = default) =>
        Send(message, options, sender: null, cancellationToken);

    public Task SendLocal(object message, SendOptions? options = null, CancellationToken cancellationToken = default) =>
        SendLocal(message, options, sender: null, cancellationToken);

    public Task Publish(object message, PublishOptions? options = null, CancellationToken cancellationToken = default) =>
        Publish(message, options, sender: null, cancellationToken);

    internal Task Send(object message, SendOptions? options, Type? sender, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        EnsureActive();
        var sendOptions = options ?? new SendOptions();
        idempotencyKey?.Stamp(sendOptions, message.GetType(), sender);
        return session.Send(message, sendOptions, cancellationToken);
    }

    internal Task SendLocal(object message, SendOptions? options, Type? sender, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        EnsureActive();
        var sendOptions = options ?? new SendOptions();
        sendOptions.RouteToThisEndpoint();
        idempotencyKey?.Stamp(sendOptions, message.GetType(), sender);
        return session.Send(message, sendOptions, cancellationToken);
    }

    internal Task Publish(object message, PublishOptions? options, Type? sender, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        EnsureActive();
        var publishOptions = options ?? new PublishOptions();
        idempotencyKey?.Stamp(publishOptions, message.GetType(), sender);
        return session.Publish(message, publishOptions, cancellationToken);
    }

    void EnsureActive()
    {
        if (!active)
        {
            throw new InvalidOperationException(
                "IOutgoingMessages can only be used in endpoints marked with WithTransactionalSession(). " +
                "Use IMessageSession to send messages outside a transactional endpoint.");
        }
    }
}

sealed class OutgoingMessages<TSender>(OutgoingMessages messages) : IOutgoingMessages<TSender>
{
    public Task Send(object message, SendOptions? options = null, CancellationToken cancellationToken = default) =>
        messages.Send(message, options, typeof(TSender), cancellationToken);

    public Task SendLocal(object message, SendOptions? options = null, CancellationToken cancellationToken = default) =>
        messages.SendLocal(message, options, typeof(TSender), cancellationToken);

    public Task Publish(object message, PublishOptions? options = null, CancellationToken cancellationToken = default) =>
        messages.Publish(message, options, typeof(TSender), cancellationToken);
}
