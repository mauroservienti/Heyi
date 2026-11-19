namespace Heyi;

using NServiceBus.TransactionalSession;

/// <summary>
/// Heyi settings, configured with <c>services.AddHeyi(options => ...)</c>.
/// </summary>
public sealed class HeyiOptions
{
    internal Func<OpenSessionOptions>? OpenSessionOptionsFactory { get; private set; }

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
}
