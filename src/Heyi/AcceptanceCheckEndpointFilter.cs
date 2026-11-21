namespace Heyi;

using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

sealed class AcceptanceCheckEndpointFilter<TCheck, TMessage> : IEndpointFilter
    where TCheck : IAcceptanceCheck<TMessage>
    where TMessage : class
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var check = ActivatorUtilities.GetServiceOrCreateInstance<TCheck>(context.HttpContext.RequestServices);

        Acceptance acceptance;

        // The span covers the check only, not the checks after it or the dispatch.
        var startTimestamp = Stopwatch.GetTimestamp();
        using (var activity = Telemetry.AcceptanceCheckStarted(typeof(TCheck)))
        {
            try
            {
                // The message is the accept-only handler's first argument.
                acceptance = await check.Check(context.GetArgument<TMessage>(0), context.HttpContext).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Telemetry.AcceptanceCheckFailed(activity, startTimestamp, typeof(TCheck), exception);
                throw;
            }

            if (acceptance.IsAccepted)
            {
                Telemetry.AcceptanceCheckAccepted(activity, startTimestamp, typeof(TCheck));
            }
            else
            {
                Telemetry.AcceptanceCheckRejected(activity, startTimestamp, typeof(TCheck));
            }
        }

        return acceptance.IsAccepted
            ? await next(context).ConfigureAwait(false)
            : acceptance.Rejection;
    }
}
