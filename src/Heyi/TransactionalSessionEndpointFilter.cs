namespace Heyi;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NServiceBus.TransactionalSession;

sealed class TransactionalSessionEndpointFilter(Func<OpenSessionOptions> openSessionOptions, ILogger<TransactionalSessionEndpointFilter> logger)
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;
        var session = httpContext.RequestServices.GetRequiredService<ITransactionalSession>();
        var outgoingMessages = httpContext.RequestServices.GetRequiredService<OutgoingMessages>();

        try
        {
            await session.Open(openSessionOptions(), httpContext.RequestAborted).ConfigureAwait(false);
            outgoingMessages.Activate();

            var result = await next(context).ConfigureAwait(false);

            if (!IsSuccessStatusCode(StatusCodeOf(result, httpContext)))
            {
                // Disposing the session without committing rolls back data and messages.
                return result;
            }

            try
            {
                // Once the endpoint has run, the commit is not tied to the client connection:
                // a client that hangs up mid-commit doesn't get to leave the outcome half done.
                await outgoingMessages.FlushTo(session, CancellationToken.None).ConfigureAwait(false);
                await session.Commit(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Failed to commit the transactional session for {Method} {Path}.", httpContext.Request.Method, httpContext.Request.Path);

                // The endpoint succeeded, but its data and messages didn't: never report a success.
                return TypedResults.Problem(
                    statusCode: StatusCodes.Status500InternalServerError,
                    title: "The request could not be completed.");
            }

            return result;
        }
        finally
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal static int StatusCodeOf(object? result, HttpContext httpContext) => result switch
    {
        INestedHttpResult nested => StatusCodeOf(nested.Result, httpContext),
        IStatusCodeHttpResult { StatusCode: int statusCode } => statusCode,
        _ => httpContext.Response.StatusCode
    };

    static bool IsSuccessStatusCode(int statusCode) => statusCode is >= 200 and <= 299;
}
