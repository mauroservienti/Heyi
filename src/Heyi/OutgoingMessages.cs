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

    public Task Send(object message, SendOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        EnsureActive();
        var sendOptions = options ?? new SendOptions();
        idempotencyKey?.Stamp(sendOptions, message.GetType());
        return session.Send(message, sendOptions, cancellationToken);
    }

    public Task SendLocal(object message, SendOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        EnsureActive();
        var sendOptions = options ?? new SendOptions();
        sendOptions.RouteToThisEndpoint();
        idempotencyKey?.Stamp(sendOptions, message.GetType());
        return session.Send(message, sendOptions, cancellationToken);
    }

    public Task Publish(object message, PublishOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        EnsureActive();
        var publishOptions = options ?? new PublishOptions();
        idempotencyKey?.Stamp(publishOptions, message.GetType());
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
