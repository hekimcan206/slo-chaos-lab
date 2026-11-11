using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace SloChaosLab.Api.Infrastructure;

internal static class Observability
{
    public const string ServiceName = "ledger-api";

    /// <summary>
    /// Structured JSON logs on stdout plus OpenTelemetry traces, metrics and logs.
    /// <list type="bullet">
    /// <item><c>OTEL_EXPORTER_OTLP_ENDPOINT</c> (set by the Aspire AppHost or compose) receives all three signals:
    /// the Aspire dashboard, used for traces and structured logs.</item>
    /// <item><c>Prometheus:OtlpEndpoint</c> receives metrics only, pushed over OTLP/HTTP to the native Prometheus
    /// receiver. Prometheus stores them, evaluates the SLO recording and burn-rate alert rules, and feeds Grafana.</item>
    /// </list>
    /// Both are optional, so the service (and the test suite) also runs without any backend.
    /// </summary>
    public static WebApplicationBuilder AddObservability(this WebApplicationBuilder builder)
    {
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(o =>
        {
            o.IncludeScopes = true;
            o.UseUtcTimestamp = true;
            o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
        });

        var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        var prometheusEndpoint = builder.Configuration["Prometheus:OtlpEndpoint"];
        var exportInterval = builder.Configuration.GetValue("Prometheus:ExportIntervalMs", 5000);
        var serviceName = builder.Configuration["OTEL_SERVICE_NAME"] ?? ServiceName;
        var serviceVersion = typeof(Observability).Assembly.GetName().Version?.ToString(3);

        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            builder.Logging.AddOpenTelemetry(logging =>
            {
                logging.IncludeFormattedMessage = true;
                logging.IncludeScopes = true;
                logging.AddOtlpExporter();
            });
        }

        // Signal-specific exporters rather than UseOtlpExporter(): the cross-cutting API cannot be combined with a
        // second, metrics-only exporter, and the SLO pipeline needs exactly that.
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName, serviceVersion: serviceVersion))
            .WithTracing(tracing =>
            {
                tracing
                    .AddAspNetCoreInstrumentation(options =>
                        // Probes fire every few seconds; tracing them adds cost and noise, not insight.
                        options.Filter = context => !context.Request.Path.StartsWithSegments("/health"))
                    .AddSource("Npgsql");
                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                {
                    tracing.AddOtlpExporter();
                }
            })
            .WithMetrics(metrics =>
            {
                // http.server.request.duration is the SLI source. Its default buckets (5 ms ... 10 s) contain the
                // 25 ms, 100 ms and 250 ms SLO thresholds exactly, so "fraction of requests under the threshold" is
                // read from a bucket boundary, not interpolated.
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddMeter("Npgsql")
                    .AddMeter(LedgerMetrics.MeterName);
                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                {
                    metrics.AddOtlpExporter();
                }

                if (!string.IsNullOrWhiteSpace(prometheusEndpoint))
                {
                    metrics.AddOtlpExporter("prometheus", (exporter, reader) =>
                    {
                        exporter.Endpoint = new Uri(prometheusEndpoint);
                        exporter.Protocol = OtlpExportProtocol.HttpProtobuf;
                        // Cumulative counters (the default) survive a lost push: the next one carries the total.
                        reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = exportInterval;
                    });
                }
            });

        builder.Services.AddSingleton<LedgerMetrics>();
        return builder;
    }
}
