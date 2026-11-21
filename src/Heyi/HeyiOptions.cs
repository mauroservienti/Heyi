namespace Heyi;

using Microsoft.AspNetCore.Http;
using NServiceBus.TransactionalSession;

/// <summary>
/// Heyi settings, configured with <c>services.AddHeyi(options => ...)</c>.
/// </summary>
public sealed class HeyiOptions
{
    internal Func<OpenSessionOptions>? OpenSessionOptionsFactory { get; private set; }

    internal bool SafeMethodsUseSessions { get; private set; }

    internal Func<HttpContext, string?>? IdempotencyKeyScope { get; private set; }

    /// <summary>
    /// Sets the persistence-specific options used to open the transactional session
    /// for every endpoint marked with <c>WithTransactionalSession()</c>.
    /// The factory is invoked once per request.
    /// </summary>
    public HeyiOptions OpenSessionOptions(Func<OpenSessionOptions> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        OpenSessionOptionsFactory = factory;
        return this;
    }

    /// <summary>
    /// Opens the transactional session for safe methods (<c>GET</c>, <c>HEAD</c>, <c>OPTIONS</c>, <c>TRACE</c>) too.
    /// By default they run without one, since reads don't need atomicity. Opt in when endpoints for safe methods
    /// might change data or send messages, so those changes are committed atomically instead of failing or getting lost.
    /// </summary>
    public HeyiOptions OpenSessionsForSafeMethods()
    {
        SafeMethodsUseSessions = true;
        return this;
    }

    /// <summary>
    /// Scopes <c>Idempotency-Key</c> values, for example per client: a non-empty scope is hashed and becomes part of
    /// every message ID derived from the key, as <c>{key}/{scope}/...</c>. Without a scope, two clients sending the same key
    /// get the same message IDs, and receivers deduplicate one client's messages away.
    /// </summary>
    /// <remarks>
    /// Return <c>null</c> to leave a request unscoped, for example when it's anonymous. The function only runs for requests
    /// that carry a key. Changing what it returns changes the message IDs, so retries that straddle the change aren't deduplicated.
    /// Applies to atomic endpoints, and to accept-only endpoints in applications that call <c>AddHeyi</c>.
    /// </remarks>
    /// <param name="scope">Returns the scope for a request, for example <c>httpContext => httpContext.User.FindFirst("sub")?.Value</c>.</param>
    public HeyiOptions ScopeIdempotencyKeys(Func<HttpContext, string?> scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        IdempotencyKeyScope = scope;
        return this;
    }
}
