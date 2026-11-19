namespace Heyi;

using NServiceBus;
using NServiceBus.TransactionalSession;

sealed class OutgoingMessages : IOutgoingMessages
{
    readonly List<Func<ITransactionalSession, CancellationToken, Task>> operations = [];
    bool active;

    internal void Activate() => active = true;

    public void Send(object message, SendOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        EnsureActive();
        var sendOptions = options ?? new SendOptions();
        operations.Add((session, cancellationToken) => session.Send(message, sendOptions, cancellationToken));
    }

    public void SendLocal(object message, SendOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        EnsureActive();
        var sendOptions = options ?? new SendOptions();
        sendOptions.RouteToThisEndpoint();
        operations.Add((session, cancellationToken) => session.Send(message, sendOptions, cancellationToken));
    }

    public void Publish(object message, PublishOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        EnsureActive();
        var publishOptions = options ?? new PublishOptions();
        operations.Add((session, cancellationToken) => session.Publish(message, publishOptions, cancellationToken));
    }

    internal async Task FlushTo(ITransactionalSession session, CancellationToken cancellationToken)
    {
        foreach (var operation in operations)
        {
            await operation(session, cancellationToken).ConfigureAwait(false);
        }

        operations.Clear();
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
