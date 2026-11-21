namespace Heyi;

using Microsoft.AspNetCore.Mvc.Filters;

/// <summary>
/// Opts a controller, a controller action, or a minimal API handler out of the transactional session,
/// for example a controller mapped with <c>MapControllers().WithTransactionalSession()</c>.
/// </summary>
/// <remarks>
/// The closest attribute wins: <see cref="WithTransactionalSessionAttribute"/> on an action opts it back in, even if
/// its controller is opted out, and the other way around. ServiceComposer composition handlers can't be opted out:
/// the endpoint filter sees the composition pipeline, not the handlers.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true)]
public sealed class WithoutTransactionalSessionAttribute : Attribute, ITransactionalSessionPolicy
{
    bool ITransactionalSessionPolicy.UsesSession => false;
}

/// <summary>
/// The MVC filters that decide whether an action runs in a transactional session: the closest one wins.
/// </summary>
interface ITransactionalSessionPolicy : IFilterMetadata
{
    bool UsesSession { get; }
}
