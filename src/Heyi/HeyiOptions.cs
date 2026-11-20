namespace Heyi;

using NServiceBus.TransactionalSession;

/// <summary>
/// Heyi settings, configured with <c>services.AddHeyi(options => ...)</c>.
/// </summary>
public sealed class HeyiOptions
{
    internal Func<OpenSessionOptions>? OpenSessionOptionsFactory { get; private set; }

    internal bool SafeMethodsUseSessions { get; private set; }

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
}
