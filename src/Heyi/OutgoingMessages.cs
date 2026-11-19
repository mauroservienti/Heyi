namespace Heyi;

using NServiceBus;
using NServiceBus.TransactionalSession;

sealed class OutgoingMessages(ITransactionalSession session) : IOutgoingMessages
{
    bool active;

    internal void Activate() => active = true;

    public Task Send(object message, SendOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        EnsureActive();
        return session.Send(message, options ?? new SendOptions(), cancellationToken);
    }

    public Task SendLocal(object message, SendOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        EnsureActive();
        var sendOptions = options ?? new SendOptions();
        sendOptions.RouteToThisEndpoint();
        return session.Send(message, sendOptions, cancellationToken);
    }

    public Task Publish(object message, PublishOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        EnsureActive();
        return session.Publish(message, options ?? new PublishOptions(), cancellationToken);
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
