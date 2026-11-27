using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace CoreBanking.Telemetry;

public static class LocalTracing
{
    public const string SourceName = "CoreBanking.Worker";
    public static readonly ActivitySource Source = new(SourceName);

    public static Activity? StartWorkerActivity(string name, ActivityKind kind, string? traceParent, ILogger logger)
    {
        var parent = default(ActivityContext);
        if (traceParent is not null &&
            (traceParent.Length != 55 || !ActivityContext.TryParse(traceParent, null, true, out parent)))
            logger.LogWarning("Contexto de rastreamento inválido; a operação terá um novo rastreamento.");
        return Source.StartActivity(name, kind, parent);
    }

    public static void AddLocalTracing(this IServiceCollection services, IConfiguration configuration,
        string serviceName, Action<TracerProviderBuilder> configure)
    {
        if (!configuration.GetValue<bool>("Tracing:Enabled")) return;
        var uri = LocalOtlp.Origin(configuration, "Tracing:OtlpEndpoint", "http://127.0.0.1:4317");
        services.AddOpenTelemetry().ConfigureResource(resource => resource.AddService(serviceName))
            .WithTracing(tracing =>
            {
                configure(tracing);
                tracing.AddOtlpExporter(options =>
                {
                    LocalOtlp.Configure(options, uri, OtlpExportProtocol.Grpc);
                    options.BatchExportProcessorOptions.MaxQueueSize = 256;
                    options.BatchExportProcessorOptions.MaxExportBatchSize = 64;
                });
            });
    }
}

internal static class LocalOtlp
{
    public static Uri Origin(IConfiguration configuration, string key, string defaultValue)
    {
        var endpoint = configuration[key] ?? defaultValue;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp ||
            !IPAddress.TryParse(uri.Host, out var host) || !IPAddress.IsLoopback(host) ||
            !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException($"{key} deve ser uma origem HTTP em endereço IP loopback.");
        return uri;
    }

    public static void Configure(OtlpExporterOptions options, Uri endpoint, OtlpExportProtocol protocol)
    {
        options.Endpoint = endpoint;
        options.Protocol = protocol;
        options.Headers = null;
        options.MaxRequestSizeBytes = 750000;
        options.MaxResponseSizeBytes = 4096;
        options.HttpClientFactory = () => new HttpClient(new HttpClientHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false
        }) { MaxResponseContentBufferSize = 4096 };
    }
}
