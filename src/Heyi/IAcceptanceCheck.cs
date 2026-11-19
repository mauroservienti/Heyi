namespace Heyi;

using Microsoft.AspNetCore.Http;

/// <summary>
/// Checks a message before an endpoint handles it, for example against a read model:
/// before an accept-only endpoint dispatches it, or before an atomic endpoint stores data and sends messages.
/// Add it to an endpoint with <c>WithAcceptanceCheck</c>. Dependencies come in through the constructor,
/// resolved from the request services.
/// </summary>
/// <remarks>
/// The check reads state that can change right after it, so the message handler still has the final say.
/// </remarks>
/// <typeparam name="TMessage">The message type the check applies to.</typeparam>
public interface IAcceptanceCheck<TMessage>
    where TMessage : class
{
    /// <summary>
    /// Accepts or rejects <paramref name="message"/>.
    /// </summary>
    /// <param name="message">The message bound from the request.</param>
    /// <param name="httpContext">The current request, for example for route values, the user, or <c>RequestAborted</c>.</param>
    ValueTask<Acceptance> Check(TMessage message, HttpContext httpContext);
}
