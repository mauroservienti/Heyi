namespace Heyi;

using System.Diagnostics;
using System.Diagnostics.Metrics;

/// <summary>
/// How a request in a transactional session ended.
/// </summary>
enum SessionOutcome
{
    Committed,
    UnsuccessfulResult,
    Exception,
    CommitFailed,
    Cancelled
}

/// <summary>
/// Heyi's traces and metrics. Span, metric, and tag names are public API: changing them is a breaking change.
/// </summary>
static class Telemetry
{
    // Telemetry is versioned independently of the package.
    static readonly ActivitySource activitySource = new(HeyiTelemetry.ActivitySourceName, "0.1.0");
    static readonly Meter meter = new(HeyiTelemetry.MeterName, "0.1.0");

    static readonly Histogram<double> sessionDuration = meter.CreateHistogram<double>(
        "heyi.session.duration",
        unit: "s",
        description: "Duration of requests in a transactional session, from opening the session to committing or rolling it back.");

    static readonly Histogram<double> acceptanceCheckDuration = meter.CreateHistogram<double>(
        "heyi.acceptance_check.duration",
        unit: "s",
        description: "Duration of acceptance checks.");

    public const string SessionActivityName = "Heyi.TransactionalSession";
    public const string AcceptanceCheckActivityName = "Heyi.AcceptanceCheck";

    public const string SessionOutcomeTag = "heyi.session.outcome";
    public const string RollbackReasonTag = "heyi.session.rollback_reason";
    public const string IdempotencyKeyPresentTag = "heyi.idempotency_key.present";
    public const string AcceptanceCheckTypeTag = "heyi.acceptance_check.type";
    public const string AcceptanceCheckOutcomeTag = "heyi.acceptance_check.outcome";

    public static Activity? SessionStarted(bool hasIdempotencyKey)
    {
        var activity = activitySource.StartActivity(SessionActivityName, ActivityKind.Internal);
        activity?.SetTag(IdempotencyKeyPresentTag, hasIdempotencyKey);
        return activity;
    }

    public static void SessionEnded(Activity? activity, long startTimestamp, SessionOutcome outcome, Exception? exception)
    {
        var (outcomeValue, rollbackReason) = outcome switch
        {
            SessionOutcome.Committed => ("committed", null),
            SessionOutcome.UnsuccessfulResult => ("rolled_back", "unsuccessful_result"),
            SessionOutcome.Exception => ("rolled_back", "exception"),
            SessionOutcome.CommitFailed => ("commit_failed", null),
            _ => ("cancelled", (string?)null)
        };

        if (activity is not null)
        {
            activity.SetTag(SessionOutcomeTag, outcomeValue);
            if (rollbackReason is not null)
            {
                activity.SetTag(RollbackReasonTag, rollbackReason);
            }

            if (exception is not null)
            {
                activity.SetStatus(ActivityStatusCode.Error, exception.Message);
                activity.AddException(exception);
            }
            else if (outcome == SessionOutcome.Committed)
            {
                activity.SetStatus(ActivityStatusCode.Ok);
            }
        }

        if (sessionDuration.Enabled)
        {
            var tags = new TagList { { SessionOutcomeTag, outcomeValue } };
            if (rollbackReason is not null)
            {
                tags.Add(RollbackReasonTag, rollbackReason);
            }

            sessionDuration.Record(Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds, tags);
        }
    }

    public static Activity? AcceptanceCheckStarted(Type checkType)
    {
        var activity = activitySource.StartActivity(AcceptanceCheckActivityName, ActivityKind.Internal);
        if (activity is { IsAllDataRequested: true })
        {
            activity.DisplayName = $"{AcceptanceCheckActivityName} {checkType.Name}";
            activity.SetTag(AcceptanceCheckTypeTag, checkType.FullName);
        }

        return activity;
    }

    public static void AcceptanceCheckAccepted(Activity? activity, long startTimestamp, Type checkType) =>
        AcceptanceCheckEnded(activity, startTimestamp, checkType, "accepted", null);

    public static void AcceptanceCheckRejected(Activity? activity, long startTimestamp, Type checkType) =>
        AcceptanceCheckEnded(activity, startTimestamp, checkType, "rejected", null);

    public static void AcceptanceCheckFailed(Activity? activity, long startTimestamp, Type checkType, Exception exception) =>
        AcceptanceCheckEnded(activity, startTimestamp, checkType, "failed", exception);

    static void AcceptanceCheckEnded(Activity? activity, long startTimestamp, Type checkType, string outcome, Exception? exception)
    {
        if (activity is not null)
        {
            activity.SetTag(AcceptanceCheckOutcomeTag, outcome);
            if (exception is not null)
            {
                activity.SetStatus(ActivityStatusCode.Error, exception.Message);
                activity.AddException(exception);
            }
            else
            {
                activity.SetStatus(ActivityStatusCode.Ok);
            }
        }

        if (acceptanceCheckDuration.Enabled)
        {
            acceptanceCheckDuration.Record(
                Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds,
                new TagList
                {
                    { AcceptanceCheckTypeTag, checkType.FullName },
                    { AcceptanceCheckOutcomeTag, outcome }
                });
        }
    }
}
