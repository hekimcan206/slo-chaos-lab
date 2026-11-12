# 2. Aspire dashboard for traces and logs, Prometheus + Grafana for SLOs; metrics pushed over OTLP

- Status: accepted
- Date: 2026-11-11

## Context

The lab needs four things: (a) traces and structured logs to explain individual slow or failed requests, (b) a
time-series store that can evaluate recording rules and multiwindow burn-rate alerts, (c) an SLO dashboard that can be
screenshotted for each postmortem, and (d) local orchestration that a reviewer can start with one command. The machine
is shared and loaded (a Docker VM with about 7.6 GB of RAM shared with another workload), so lighter wins when the
capabilities are equal. The brief allowed either "Aspire dashboard plus Prometheus/Grafana" or "Grafana alone".

## Options considered

1. **Aspire dashboard only.** Light (239 MB image, no state), excellent for traces, logs and live metrics, and it comes
   free with the AppHost. But it keeps telemetry in memory, has no query language, no recording rules and no alerting:
   it cannot compute a burn rate, let alone page on one.
2. **"Grafana alone": the `grafana/otel-lgtm` all-in-one image** (OpenTelemetry Collector, Prometheus, Tempo, Loki,
   Pyroscope and Grafana in one container). One container, but six processes; Grafana describes it as a development
   and demo backend, and community setups give it a 4 GB memory limit. It would replace the Aspire dashboard that the
   brief asks for anyway, and Tempo + Loki duplicate what that dashboard already does for traces and logs.
3. **Aspire dashboard + Prometheus + Grafana, with the service pushing metrics to Prometheus over OTLP.** Prometheus
   (v3, native OTLP receiver via `--web.enable-otlp-receiver`) stores metrics and evaluates the rules; Grafana only
   draws; traces and logs stay in the Aspire dashboard. No OpenTelemetry Collector: the .NET SDK exports to both
   backends directly (two OTLP exporters on the metrics pipeline).

## Decision

Option 3. Measured on this machine during a 50 req/s experiment (`docker stats`, steady state): Prometheus 38 MiB,
Toxiproxy 15 MiB, the API 93 MiB, Grafana 546 MiB (Grafana 13 unpacks its bundled data-source plugins into
`/var/lib/grafana`, which is a tmpfs here and therefore counted as memory). Images: Prometheus 381 MB, Aspire dashboard
239 MB, Grafana 1.96 GB. The all-in-one image was not pulled (disk is short), so its footprint above comes from its
documentation, not from a measurement. The deciding argument is capability per process: option 3 runs exactly one
component per job, and everything the SLO math needs lives in Prometheus rules that are unit-tested with promtool.
Grafana is optional at runtime (CI's chaos smoke test runs without it, and without the Aspire dashboard).

Details:
- Prometheus evaluates every 5 s and the service pushes every 1 s in the lab (5 s by default); production would use
  15-60 s with the same rules.
- Each experiment recreates Prometheus on a tmpfs (fresh TSDB), see ADR 0006 for why and for how that bias is corrected.
- The dashboard is code (`tools/dashboard/build-dashboard.mjs`); CI fails if the committed JSON differs from it.

## Consequences

- Two telemetry destinations to configure, and the cross-cutting `UseOtlpExporter()` cannot be used (it refuses a
  second, signal-specific exporter), so exporters are registered per signal.
- No Alertmanager: alerts are evaluated and visible in Prometheus and Grafana, and asserted by the CI smoke test, but
  not routed to a pager. Routing is a deployment concern, listed under Limitations.

## Sources

- Prometheus, "Using Prometheus as your OpenTelemetry backend": https://prometheus.io/docs/guides/opentelemetry/
- Grafana, docker-otel-lgtm: https://github.com/grafana/docker-otel-lgtm and https://grafana.com/docs/opentelemetry/docker-lgtm/
- .NET Aspire standalone dashboard: https://learn.microsoft.com/dotnet/aspire/fundamentals/dashboard/standalone
