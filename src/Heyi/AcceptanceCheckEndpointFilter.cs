namespace Heyi;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

sealed class AcceptanceCheckEndpointFilter<TCheck, TMessage>(int messageIndex) : IEndpointFilter
    where TCheck : IAcceptanceCheck<TMessage>
    where TMessage : class
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var check = ActivatorUtilities.GetServiceOrCreateInstance<TCheck>(context.HttpContext.RequestServices);
        var acceptance = await check.Check(context.GetArgument<TMessage>(messageIndex), context.HttpContext).ConfigureAwait(false);

        return acceptance.IsAccepted
            ? await next(context).ConfigureAwait(false)
            : acceptance.Rejection;
    }
}
