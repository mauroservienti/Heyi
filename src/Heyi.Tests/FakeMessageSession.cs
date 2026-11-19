namespace Heyi.Tests;

using NServiceBus;

sealed class FakeMessageSession : IMessageSession
{
    public List<object> Sent { get; } = [];
    public List<object> Published { get; } = [];
    public Exception? DispatchFailure { get; set; }

    public Task Send(object message, SendOptions sendOptions, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing();
        Sent.Add(message);
        return Task.CompletedTask;
    }

    public Task Send<T>(Action<T> messageConstructor, SendOptions sendOptions, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task Publish(object message, PublishOptions publishOptions, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing();
        Published.Add(message);
        return Task.CompletedTask;
    }

    public Task Publish<T>(Action<T> messageConstructor, PublishOptions publishOptions, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task Subscribe(Type eventType, SubscribeOptions subscribeOptions, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task Unsubscribe(Type eventType, UnsubscribeOptions unsubscribeOptions, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    void ThrowIfFailing()
    {
        if (DispatchFailure is not null)
        {
            throw DispatchFailure;
        }
    }
}
