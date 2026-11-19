namespace Heyi.Tests;

using NServiceBus;
using NServiceBus.Testing;

sealed class FakeMessageSession : TestableMessageSession
{
    public Exception? DispatchFailure { get; set; }

    public override Task Send(object message, SendOptions sendOptions, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing();
        return base.Send(message, sendOptions, cancellationToken);
    }

    public override Task Publish(object message, PublishOptions publishOptions, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing();
        return base.Publish(message, publishOptions, cancellationToken);
    }

    void ThrowIfFailing()
    {
        if (DispatchFailure is not null)
        {
            throw DispatchFailure;
        }
    }
}
