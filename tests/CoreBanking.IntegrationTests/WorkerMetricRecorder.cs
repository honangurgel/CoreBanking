using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using CoreBanking.Worker;

namespace CoreBanking.IntegrationTests;

internal sealed class WorkerMetricRecorder : IDisposable
{
    private readonly MeterListener listener = new();
    public ConcurrentQueue<(string Name, long Value, string? Tag)> Counts { get; } = new();
    public ConcurrentQueue<(string Name, double Value)> Durations { get; } = new();

    public WorkerMetricRecorder()
    {
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == WorkerMetrics.MeterName)
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            string? tag = null;
            foreach (var item in tags)
                if (item.Key is "status" or "outcome" or "queue")
                    tag = item.Value as string;
            Counts.Enqueue((instrument.Name, value, tag));
        });
        listener.SetMeasurementEventCallback<double>((instrument, value, _, _) =>
            Durations.Enqueue((instrument.Name, value)));
        listener.Start();
    }

    public void Dispose() => listener.Dispose();
}
