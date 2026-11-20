namespace Microsoft.AspNetCore.Builder;

using Heyi;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NServiceBus;

/// <summary>
/// Maps accept-only endpoints: the request body becomes a message, and a message handler does the work.
/// </summary>
public static class AcceptOnlyEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps a POST endpoint that binds the request body to <typeparamref name="TCommand"/>, sends it,
    /// and returns <c>202 Accepted</c>. The destination comes from the NServiceBus routing configuration.
    /// Use <c>WithAcceptanceCheck</c> to check the message before it's sent.
    /// </summary>
    /// <param name="endpoints">The route builder.</param>
    /// <param name="pattern">The route pattern.</param>
    /// <param name="location">Builds the location to poll, returned in the <c>Location</c> header.</param>
    public static AcceptOnlyEndpointBuilder<TCommand> MapCommand<TCommand>(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        Func<TCommand, string>? location = null)
        where TCommand : class =>
        endpoints.MapAccepted(pattern, location, (session, command, idempotencyKey, cancellationToken) =>
        {
            var options = new SendOptions();
            idempotencyKey?.Stamp(options, command.GetType());
            return session.Send(command, options, cancellationToken);
        });

    /// <summary>
    /// Maps a POST endpoint that binds the request body to <typeparamref name="TEvent"/>, publishes it,
    /// and returns <c>202 Accepted</c>. Use <c>WithAcceptanceCheck</c> to check the message before it's published.
    /// </summary>
    /// <param name="endpoints">The route builder.</param>
    /// <param name="pattern">The route pattern.</param>
    /// <param name="location">Builds the location to poll, returned in the <c>Location</c> header.</param>
    public static AcceptOnlyEndpointBuilder<TEvent> MapEvent<TEvent>(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        Func<TEvent, string>? location = null)
        where TEvent : class =>
        endpoints.MapAccepted(pattern, location, (session, @event, idempotencyKey, cancellationToken) =>
        {
            var options = new PublishOptions();
            idempotencyKey?.Stamp(options, @event.GetType());
            return session.Publish(@event, options, cancellationToken);
        });

    static AcceptOnlyEndpointBuilder<TMessage> MapAccepted<TMessage>(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        Func<TMessage, string>? location,
        Func<IMessageSession, TMessage, IdempotencyKey?, CancellationToken, Task> dispatch)
        where TMessage : class
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrEmpty(pattern);

        var builder = endpoints.MapPost(pattern, async Task<Results<Accepted, ProblemHttpResult>> ([FromBody] TMessage message, HttpContext httpContext) =>
        {
            if (!IdempotencyKey.TryRead(httpContext.Request, out var idempotencyKey))
            {
                return IdempotencyKey.Invalid();
            }

            var session = httpContext.RequestServices.GetRequiredService<IMessageSession>();

            try
            {
                await dispatch(session, message, idempotencyKey, httpContext.RequestAborted).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !httpContext.RequestAborted.IsCancellationRequested)
            {
                httpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger(typeof(AcceptOnlyEndpointRouteBuilderExtensions))
                    .LogError(exception, "Failed to dispatch {MessageType} for {Method} {Path}.", typeof(TMessage).FullName, httpContext.Request.Method, httpContext.Request.Path);

                return TypedResults.Problem(
                    statusCode: StatusCodes.Status500InternalServerError,
                    title: "The request could not be accepted.");
            }

            return TypedResults.Accepted(location?.Invoke(message));
        });

        builder.AddEndpointFilterFactory((context, next) =>
        {
            var isService = context.ApplicationServices.GetRequiredService<IServiceProviderIsService>();
            if (!isService.IsService(typeof(IMessageSession)))
            {
                throw new InvalidOperationException(
                    "No IMessageSession is registered. Register the NServiceBus endpoint with AddNServiceBusEndpoint before mapping accept-only endpoints.");
            }

            return next;
        });

        return new AcceptOnlyEndpointBuilder<TMessage>(builder);
    }
}
