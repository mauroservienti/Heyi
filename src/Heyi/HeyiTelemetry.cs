namespace Heyi;

/// <summary>
/// The names of Heyi's OpenTelemetry sources. Heyi emits traces and metrics only when they're collected:
/// add <see cref="ActivitySourceName"/> to the tracer provider and <see cref="MeterName"/> to the meter provider.
/// </summary>
public static class HeyiTelemetry
{
    /// <summary>
    /// The name of the activity source for transactional sessions and acceptance checks.
    /// </summary>
    public const string ActivitySourceName = "Heyi";

    /// <summary>
    /// The name of the meter for transactional sessions and acceptance checks.
    /// </summary>
    public const string MeterName = "Heyi";
}
