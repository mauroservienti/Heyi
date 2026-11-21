namespace Heyi;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

/// <summary>
/// Wraps a controller action, or all the actions of a controller, in an NServiceBus transactional session,
/// with the same rules as <c>WithTransactionalSession()</c>, and without any endpoint configuration.
/// Use it to choose which controllers or actions are transactional; <c>MapControllers().WithTransactionalSession()</c>
/// wraps them all.
/// </summary>
/// <remarks>
/// It's an MVC filter: it applies to controllers only. Minimal API endpoints and ServiceComposer composition handlers
/// use <c>WithTransactionalSession()</c>. Marked actions that are also mapped with <c>WithTransactionalSession()</c>
/// run in a single session. The open session options are the ones registered with <c>AddHeyi</c>.
/// The closest attribute wins: <see cref="WithoutTransactionalSessionAttribute"/> on an action opts it out of a marked controller.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true)]
public sealed class WithTransactionalSessionAttribute : Attribute, IFilterFactory
{
    /// <inheritdoc />
    public bool IsReusable => true;

    /// <inheritdoc />
    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider) =>
        new TransactionalSessionActionFilter(TransactionalSessionEndpointFilter.Create(serviceProvider, openSessionOptions: null));
}

sealed class TransactionalSessionActionFilter(TransactionalSessionEndpointFilter filter) : IAsyncActionFilter, ITransactionalSessionPolicy
{
    public bool UsesSession => true;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        // The closest attribute wins: a [WithTransactionalSession] controller can have [WithoutTransactionalSession] actions,
        // and an action marked too has its own filter.
        if (!ReferenceEquals(context.FindEffectivePolicy<ITransactionalSessionPolicy>(), this))
        {
            await next().ConfigureAwait(false);
            return;
        }

        ActionExecutedContext? executed = null;
        object? result;

        try
        {
            result = await filter.Run(context.HttpContext, async () =>
            {
                executed = await next().ConfigureAwait(false);

                // MVC reports action exceptions on the context instead of throwing them: rethrow so the session rolls back.
                if (executed.Exception is { } exception && !executed.ExceptionHandled)
                {
                    throw new ActionFailedException(exception);
                }

                return executed.Result;
            }).ConfigureAwait(false);
        }
        catch (ActionFailedException)
        {
            // Rolled back. The exception is still on the executed context, for MVC to handle as usual.
            return;
        }

        if (executed is null)
        {
            // The action didn't run, for example because the Idempotency-Key is invalid.
            context.Result = AsActionResult(result);
        }
        else if (!ReferenceEquals(result, executed.Result))
        {
            // The commit failed: the action's result is replaced.
            executed.Result = AsActionResult(result);
        }
    }

    static IActionResult? AsActionResult(object? result) => result switch
    {
        IActionResult actionResult => actionResult,
        IResult httpResult => new HttpResultActionResult(httpResult),
        _ => null
    };

    sealed class HttpResultActionResult(IResult result) : IActionResult
    {
        public Task ExecuteResultAsync(ActionContext context) => result.ExecuteAsync(context.HttpContext);
    }

    sealed class ActionFailedException(Exception inner) : Exception(inner.Message, inner);
}
