namespace Heyi.Tests;

using NServiceBus;
using NServiceBus.Extensibility;
using NServiceBus.Persistence;
using NServiceBus.TransactionalSession;

sealed class FakeTransactionalSession : ITransactionalSession
{
    public OpenSessionOptions? OpenedWith { get; private set; }
    public bool Committed { get; private set; }
    public bool Disposed { get; private set; }
    public Exception? CommitException { get; set; }
    public List<(object Message, string Kind, ExtendableOptions Options)> Operations { get; } = [];

    public string SessionId { get; } = Guid.NewGuid().ToString();
    // Like SQL persistence, the storage session and its transaction start when the session is opened.
    public Func<FakeSqlStorageSession>? StorageSessionFactory { get; set; }
    public FakeSqlStorageSession? StorageSession { get; private set; }

    public ISynchronizedStorageSession SynchronizedStorageSession =>
        StorageSession ?? (ISynchronizedStorageSession)new NonSqlStorageSession();

    public Task Open(OpenSessionOptions options, CancellationToken cancellationToken = default)
    {
        // Like the real session, it can't be opened twice.
        if (OpenedWith is not null)
        {
            throw new InvalidOperationException("This session is already open.");
        }

        OpenedWith = options;
        StorageSession = StorageSessionFactory?.Invoke();
        return Task.CompletedTask;
    }

    public Task Send(object message, SendOptions sendOptions, CancellationToken cancellationToken = default)
    {
        // Like the real session, it can be used concurrently, for example by ServiceComposer's handlers.
        lock (Operations)
        {
            Operations.Add((message, "Send", sendOptions));
        }

        return Task.CompletedTask;
    }

    public Task Send<T>(Action<T> messageConstructor, SendOptions sendOptions, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task Publish(object message, PublishOptions publishOptions, CancellationToken cancellationToken = default)
    {
        // Like the real session, it can be used concurrently, for example by ServiceComposer's handlers.
        lock (Operations)
        {
            Operations.Add((message, "Publish", publishOptions));
        }

        return Task.CompletedTask;
    }

    public Task Publish<T>(Action<T> messageConstructor, PublishOptions publishOptions, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public TaskCompletionSource? CommitStarted { get; set; }
    public TaskCompletionSource CommitCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task Commit(CancellationToken cancellationToken = default)
    {
        if (CommitException is not null)
        {
            throw CommitException;
        }

        if (CommitStarted is not null)
        {
            // Simulates a slow commit: it completes after a while, unless the request is aborted first.
            CommitStarted.SetResult();
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                CommitCancelled.SetResult();
                throw;
            }
        }

        if (StorageSession is not null)
        {
            await StorageSession.Commit(cancellationToken);
        }

        Committed = true;
    }

    public void Dispose()
    {
        StorageSession?.Dispose();
        Disposed = true;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

sealed class NonSqlStorageSession : ISynchronizedStorageSession;

sealed class TestOpenSessionOptions : OpenSessionOptions
{
    public string Name { get; init; } = "default";
}

record PlaceOrder(int OrderId);

record OrderPlaced(int OrderId);
