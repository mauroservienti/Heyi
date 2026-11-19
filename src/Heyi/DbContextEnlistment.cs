namespace Heyi;

using Microsoft.EntityFrameworkCore;
using NServiceBus.Persistence.Sql;
using NServiceBus.TransactionalSession;

sealed class DbContextEnlistment<TContext>(TContext context) : IStorageSessionEnlistment
    where TContext : DbContext
{
    public void Enlist(ITransactionalSession session)
    {
        if (session.SynchronizedStorageSession is not ISqlStorageSession storageSession)
        {
            throw new InvalidOperationException(
                $"AddHeyiDbContext<{typeof(TContext).Name}>() requires the NServiceBus SQL persistence with the transactional session enabled.");
        }

        // The context is the same request-scoped instance the endpoint gets, even if it was resolved
        // before the session was opened, for example as an endpoint parameter.
        var database = context.Database;
        database.SetDbConnection(storageSession.Connection, contextOwnsConnection: false);
        database.UseTransaction(storageSession.Transaction);

        // Pending changes are saved right before the session commits its transaction.
        storageSession.OnSaveChanges((_, cancellationToken) => context.SaveChangesAsync(cancellationToken));
    }
}
