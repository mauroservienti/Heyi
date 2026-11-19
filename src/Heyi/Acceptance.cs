namespace Heyi;

using Microsoft.AspNetCore.Http;

/// <summary>
/// The outcome of an <see cref="IAcceptanceCheck{TMessage}"/>: accept the message, or reject it with a response.
/// </summary>
public sealed class Acceptance
{
    static readonly Acceptance accepted = new(null);

    Acceptance(IResult? rejection) => Rejection = rejection;

    /// <summary>
    /// The response to return instead of dispatching the message, or <c>null</c> when the message is accepted.
    /// </summary>
    public IResult? Rejection { get; }

    /// <summary>
    /// Whether the message is accepted and can be dispatched.
    /// </summary>
    public bool IsAccepted => Rejection is null;

    /// <summary>
    /// Accepts the message: the next check runs, or the endpoint does.
    /// </summary>
    public static Acceptance Accept() => accepted;

    /// <summary>
    /// Rejects the message: <paramref name="response"/> is returned, typically a <c>4xx</c> problem details response, and the endpoint doesn't run.
    /// </summary>
    /// <param name="response">The response to return.</param>
    public static Acceptance Reject(IResult response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return new Acceptance(response);
    }
}
