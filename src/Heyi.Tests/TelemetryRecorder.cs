namespace Heyi.Tests;

using System.Diagnostics;
using System.Diagnostics.Metrics;

/// <summary>
/// Records Heyi's activities and measurements, as an OpenTelemetry SDK subscribed to its sources would.
/// </summary>
sealed class TelemetryRecorder : IDisposable
{
    readonly ActivityListener activityListener;
    readonly MeterListener meterListener = new();

    public TelemetryRecorder()
    {
        activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == HeyiTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (Activities)
                {
                    Activities.Add(activity);
                }
            }
        };
        ActivitySource.AddActivityListener(activityListener);

        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == HeyiTelemetry.MeterName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            lock (Measurements)
            {
                Measurements.Add((instrument.Name, value, tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value)));
            }
        });
        meterListener.Start();
    }

    public List<Activity> Activities { get; } = [];

    public List<(string Instrument, double Value, Dictionary<string, object?> Tags)> Measurements { get; } = [];

    public Activity Single(string operationName) => Activities.Single(activity => activity.OperationName == operationName);

    public void Dispose()
    {
        activityListener.Dispose();
        meterListener.Dispose();
    }
}
