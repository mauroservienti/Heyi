namespace Heyi.Tests;

using System.Data.Common;
using Microsoft.Data.Sqlite;
using NServiceBus.Persistence;
using NServiceBus.Persistence.Sql;

/// <summary>
/// A SQL storage session over a real SQLite connection and transaction, like the one SQL persistence
/// opens for the transactional session: callbacks run before the commit, and disposing rolls back.
/// </summary>
sealed class FakeSqlStorageSession : ISynchronizedStorageSession, ISqlStorageSession, IDisposable
{
    readonly SqliteConnection connection;
    readonly SqliteTransaction transaction;
    readonly List<Func<ISqlStorageSession, CancellationToken, Task>> callbacks = [];

    public FakeSqlStorageSession(string connectionString)
    {
        connection = new SqliteConnection(connectionString);
        connection.Open();
        transaction = connection.BeginTransaction();
    }

    public DbConnection Connection => connection;
    public DbTransaction Transaction => transaction;

    public void OnSaveChanges(Func<ISqlStorageSession, CancellationToken, Task> callback) => callbacks.Add(callback);

    public async Task Commit(CancellationToken cancellationToken)
    {
        foreach (var callback in callbacks)
        {
            await callback(this, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public void Dispose()
    {
        transaction.Dispose();
        connection.Dispose();
    }
}
