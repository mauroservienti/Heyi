namespace Heyi;

using NServiceBus.TransactionalSession;

/// <summary>
/// Joins request-scoped data access to the transactional session, right after it's opened.
/// </summary>
interface IStorageSessionEnlistment
{
    void Enlist(ITransactionalSession session);
}
