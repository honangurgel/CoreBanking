using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace CoreBanking.Telemetry;

public static class LocalMetrics
{
    public static void AddLocalWorkerMetrics(this IServiceCollection services, IConfiguration configuration)
    {
        if (!configuration.GetValue<bool>("Metrics:Enabled")) return;
        var origin = LocalOtlp.Origin(configuration, "Metrics:OtlpEndpoint", "http://127.0.0.1:9090");
        var endpoint = new Uri(origin, "/api/v1/otlp/v1/metrics");
        services.AddOpenTelemetry().ConfigureResource(resource => resource.AddService("corebanking-worker"))
            .WithMetrics(metrics => metrics.AddMeter("CoreBanking.Worker")
                .AddView("corebanking.pix.simulator.duration", new ExplicitBucketHistogramConfiguration
                {
                    Boundaries = [0.01, 0.05, 0.1, 0.25, 0.5, 1, 2, 5, 10, 30]
                })
                .AddView("corebanking.rabbitmq.delivery.age", new ExplicitBucketHistogramConfiguration
                {
                    Boundaries = [1, 2, 5, 10, 30, 60, 120, 300, 600, 1800]
                })
                .AddOtlpExporter((options, reader) =>
                {
                    LocalOtlp.Configure(options, endpoint, OtlpExportProtocol.HttpProtobuf);
                    reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 15000;
                }));
    }
}
