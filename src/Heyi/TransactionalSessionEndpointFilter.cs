namespace Heyi;

using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NServiceBus.TransactionalSession;

sealed class TransactionalSessionEndpointFilter(Func<OpenSessionOptions> openSessionOptions, bool safeMethodsUseSessions, Func<HttpContext, string?>? idempotencyKeyScope, ILogger<TransactionalSessionEndpointFilter> logger)
{
    /// <summary>
    /// Creates the filter, checking that Heyi and the transactional session are registered.
    /// </summary>
    public static TransactionalSessionEndpointFilter Create(IServiceProvider services, Func<OpenSessionOptions>? openSessionOptions)
    {
        var options = services.GetService<HeyiOptions>()
            ?? throw new InvalidOperationException(
                "Heyi isn't registered. Call services.AddHeyi() after configuring the NServiceBus endpoint.");

        var isService = services.GetRequiredService<IServiceProviderIsService>();
        if (!isService.IsService(typeof(ITransactionalSession)))
        {
            throw new InvalidOperationException(
                "No ITransactionalSession is registered. Enable the transactional session on the NServiceBus persistence " +
                "(persistence.EnableTransactionalSession()), enable the outbox, and register the endpoint with AddNServiceBusEndpoint before mapping transactional endpoints.");
        }

        var factory = openSessionOptions ?? options.OpenSessionOptionsFactory
            ?? throw new InvalidOperationException(
                "No open session options are configured. Use services.AddHeyi(options => options.OpenSessionOptions(() => new ...)) " +
                "or pass them to WithTransactionalSession(() => new ...).");

        return new TransactionalSessionEndpointFilter(
            factory,
            options.SafeMethodsUseSessions,
            options.IdempotencyKeyScope,
            services.GetRequiredService<ILogger<TransactionalSessionEndpointFilter>>());
    }

    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        Run(context.HttpContext, () => next(context));

    /// <summary>
    /// Runs <paramref name="next"/> in a transactional session, and returns its result, or the response to send instead.
    /// </summary>
    public async ValueTask<object?> Run(HttpContext httpContext, Func<ValueTask<object?>> next)
    {
        // Reads don't need atomicity: unless opted in, endpoints for safe methods run without a session,
        // and IOutgoingMessages throws if they try to send.
        if (!safeMethodsUseSessions && IsSafeMethod(httpContext.Request.Method))
        {
            return await next().ConfigureAwait(false);
        }

        var outgoingMessages = httpContext.RequestServices.GetRequiredService<OutgoingMessages>();

        // Already in a session, for example a [WithTransactionalSession] controller mapped with WithTransactionalSession():
        // the outer one owns it.
        if (outgoingMessages.IsActive)
        {
            return await next().ConfigureAwait(false);
        }

        var session = httpContext.RequestServices.GetRequiredService<ITransactionalSession>();

        if (!IdempotencyKey.TryRead(httpContext.Request, idempotencyKeyScope, out var idempotencyKey))
        {
            return IdempotencyKey.Invalid();
        }

        var startTimestamp = Stopwatch.GetTimestamp();
        using var activity = Telemetry.SessionStarted(idempotencyKey is not null);
        var outcome = SessionOutcome.Exception;
        Exception? failure = null;

        try
        {
            await session.Open(openSessionOptions(), httpContext.RequestAborted).ConfigureAwait(false);

            foreach (var enlistment in httpContext.RequestServices.GetServices<IStorageSessionEnlistment>())
            {
                enlistment.Enlist(session);
            }

            outgoingMessages.Activate(idempotencyKey);

            var result = await next().ConfigureAwait(false);

            if (!IsSuccessStatusCode(StatusCodeOf(result, httpContext)))
            {
                // Disposing the session without committing rolls back data and messages.
                outcome = SessionOutcome.UnsuccessfulResult;
                return result;
            }

            try
            {
                // A client that hangs up cancels the commit too. The transactional session
                // guarantees an atomic outcome either way: all of it, or none of it.
                await session.Commit(httpContext.RequestAborted).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !httpContext.RequestAborted.IsCancellationRequested)
            {
                logger.LogError(exception, "Failed to commit the transactional session for {Method} {Path}.", httpContext.Request.Method, httpContext.Request.Path);
                outcome = SessionOutcome.CommitFailed;
                failure = exception;

                // The endpoint succeeded, but its data and messages didn't: never report a success.
                return TypedResults.Problem(
                    statusCode: StatusCodes.Status500InternalServerError,
                    title: "The request could not be completed.");
            }
            catch (OperationCanceledException)
            {
                outcome = SessionOutcome.Cancelled;
                throw;
            }

            outcome = SessionOutcome.Committed;
            return result;
        }
        catch (Exception exception) when (outcome == SessionOutcome.Exception)
        {
            // Opening the session or running the endpoint failed. Controller action exceptions arrive wrapped.
            failure = exception is TransactionalSessionActionFilter.ActionFailedException { InnerException: { } actionException } ? actionException : exception;
            throw;
        }
        finally
        {
            await session.DisposeAsync().ConfigureAwait(false);
            Telemetry.SessionEnded(activity, startTimestamp, outcome, failure);
        }
    }

    internal static int StatusCodeOf(object? result, HttpContext httpContext) => result switch
    {
        INestedHttpResult nested => StatusCodeOf(nested.Result, httpContext),
        IStatusCodeHttpResult { StatusCode: int statusCode } => statusCode,
        // MVC-style results, for example the ones ServiceComposer's SetActionResult returns:
        // they run after the filter, so the response status code doesn't reflect them yet.
        IStatusCodeActionResult { StatusCode: int statusCode } => statusCode,
        _ => httpContext.Response.StatusCode
    };

    static bool IsSafeMethod(string method) =>
        HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method);

    static bool IsSuccessStatusCode(int statusCode) => statusCode is >= 200 and <= 299;
}
