namespace Heyi;

/// <summary>
/// Marks a controller, a controller action, or a minimal API handler as transactional, for endpoints
/// mapped with <c>WithTransactionalSessionAttributes()</c>: marked endpoints are wrapped in a transactional session,
/// the others run without one. On a controller, it applies to all its actions, including inherited ones.
/// </summary>
/// <remarks>
/// ServiceComposer composition handlers can't be marked: the endpoint filter sees the composition pipeline,
/// not the handlers. Use <c>MapCompositionHandlers().WithTransactionalSession()</c> instead.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true)]
public sealed class TransactionalSessionAttribute : Attribute;
