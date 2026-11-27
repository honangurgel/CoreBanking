using System.Diagnostics.Metrics;

namespace CoreBanking.Worker;

public static class WorkerMetrics
{
    public const string MeterName = "CoreBanking.Worker";
    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> PixProcessed = Meter.CreateCounter<long>("corebanking.pix.processed", "{operation}");
    private static readonly Counter<long> PixThrottled = Meter.CreateCounter<long>("corebanking.pix.throttled", "{attempt}");
    private static readonly Histogram<double> SimulatorDuration = Meter.CreateHistogram<double>("corebanking.pix.simulator.duration", "s");
    private static readonly Counter<long> TransferMessages = Meter.CreateCounter<long>("corebanking.transfer.messages", "{message}");
    private static readonly Histogram<double> DeliveryAge = Meter.CreateHistogram<double>("corebanking.rabbitmq.delivery.age", "s");
    private static readonly Counter<long> DeliveryAgeUnavailable = Meter.CreateCounter<long>("corebanking.rabbitmq.delivery.age.unavailable", "{message}");
    private static readonly Gauge<long> BacklogCount = Meter.CreateGauge<long>("corebanking.backlog.count", "{item}");
    private static readonly Gauge<double> BacklogAge = Meter.CreateGauge<double>("corebanking.backlog.oldest.age", "s");
    private static readonly Gauge<long> RabbitReady = Meter.CreateGauge<long>("corebanking.rabbitmq.queue.ready", "{message}");
    private static readonly Gauge<long> LedgerAccountsChecked = Meter.CreateGauge<long>("corebanking.ledger.reconciliation.accounts", "{account}");
    private static readonly Gauge<long> LedgerMismatches = Meter.CreateGauge<long>("corebanking.ledger.reconciliation.mismatches", "{account}");
    private static readonly Gauge<long> LedgerScanStarted = Meter.CreateGauge<long>("corebanking.ledger.reconciliation.started", "s");
    private static readonly Gauge<long> LedgerScanCompleted = Meter.CreateGauge<long>("corebanking.ledger.reconciliation.completed", "s");

    public static bool BacklogEnabled => BacklogCount.Enabled || BacklogAge.Enabled;
    public static bool RabbitReadyEnabled => RabbitReady.Enabled;
    public static bool LedgerReconciliationEnabled =>
        LedgerAccountsChecked.Enabled || LedgerMismatches.Enabled || LedgerScanStarted.Enabled || LedgerScanCompleted.Enabled;
    public static void RecordPix(string status) => PixProcessed.Add(1, new KeyValuePair<string, object?>("status", status));
    public static void RecordThrottle() => PixThrottled.Add(1);
    public static void RecordSimulatorDuration(double seconds) => SimulatorDuration.Record(seconds);
    public static void RecordTransfer(string outcome) => TransferMessages.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
    public static void RecordDeliveryAge(double seconds) => DeliveryAge.Record(seconds);
    public static void RecordDeliveryAgeUnavailable() => DeliveryAgeUnavailable.Add(1);
    public static void RecordBacklog(string queue, long count, double ageSeconds)
    {
        var tag = new KeyValuePair<string, object?>("queue", queue);
        BacklogCount.Record(count, tag);
        BacklogAge.Record(ageSeconds, tag);
    }
    public static void RecordRabbitReady(string queue, long count) =>
        RabbitReady.Record(count, new KeyValuePair<string, object?>("queue", queue));
    public static void RecordLedgerReconciliation(long accounts, long mismatches, long started, long completed)
    {
        LedgerAccountsChecked.Record(accounts);
        LedgerMismatches.Record(mismatches);
        LedgerScanStarted.Record(started);
        LedgerScanCompleted.Record(completed);
    }
}
