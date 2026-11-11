// Builds the Grafana dashboard as code: node tools/dashboard/build-dashboard.mjs > deploy/grafana/dashboards/slo.json
// The JSON is committed (Grafana provisions it read-only); edit this file, not the JSON.
const SEL = 'job="ledger-api",http_route="/api/v1/journal-entries",http_request_method="POST"';
const POOL = 'db_client_connection_pool_name="request-path"';
let id = 1;
const ds = { type: "prometheus", uid: "prometheus" };
const t = (expr, legend) => ({ datasource: ds, expr, legendFormat: legend });
const stat = (title, x, y, w, targets, unit, thresholds, decimals = 3) => ({
  id: id++, type: "stat", title, datasource: ds, gridPos: { x, y, w, h: 4 },
  targets, options: { reduceOptions: { calcs: ["lastNotNull"] }, colorMode: "background", graphMode: "area", textMode: "value_and_name" },
  fieldConfig: { defaults: { unit, decimals, thresholds: { mode: "absolute", steps: thresholds } }, overrides: [] },
});
const ts = (title, x, y, w, h, targets, unit, extra = {}) => ({
  id: id++, type: "timeseries", title, datasource: ds, gridPos: { x, y, w, h }, targets,
  options: { legend: { displayMode: "list", placement: "bottom" }, tooltip: { mode: "multi" } },
  fieldConfig: { defaults: { unit, custom: { lineWidth: 2, fillOpacity: 0, showPoints: "never", ...(extra.custom ?? {}) }, thresholds: extra.thresholds ?? { mode: "absolute", steps: [{ color: "green", value: null }] }, ...(extra.defaults ?? {}) }, overrides: extra.overrides ?? [] },
  description: extra.description,
});
const lines = (steps) => ({ custom: { thresholdsStyle: { mode: "line+area" } }, thresholds: { mode: "absolute", steps } });
const panels = [
  stat("Availability, 5 m (SLO 99.9 %)", 0, 0, 5, [t('1 - slo:sli_error:ratio_rate5m{slo="ledger-post-availability"}', "availability")], "percentunit",
    [{ color: "red", value: null }, { color: "green", value: 0.999 }]),
  stat("Within 250 ms, 5 m (SLO 99 %)", 5, 0, 5, [t('1 - slo:sli_error:ratio_rate5m{slo="ledger-post-latency-p99"}', "<=250 ms")], "percentunit",
    [{ color: "red", value: null }, { color: "green", value: 0.99 }]),
  stat("Error budget remaining (data in window)", 10, 0, 7, [
    t('slo:error_budget_remaining:ratio{slo="ledger-post-availability"}', "availability"),
    t('slo:error_budget_remaining:ratio{slo="ledger-post-latency-p99"}', "latency p99")], "percentunit",
    [{ color: "red", value: null }, { color: "orange", value: 0.25 }, { color: "green", value: 0.5 }], 1),
  stat("Firing alerts", 17, 0, 7, [t('count(ALERTS{alertstate="firing"}) or vector(0)', "firing")], "none",
    [{ color: "green", value: null }, { color: "red", value: 1 }], 0),
  ts("Requests per second by status", 0, 4, 12, 8, [t(`sum by (http_response_status_code) (rate(http_server_request_duration_seconds_count{${SEL}}[30s]))`, "{{http_response_status_code}}")], "reqps"),
  ts("Latency of successful posts (p50 / p95 / p99, 1 m)", 12, 4, 12, 8, [
    t("slo:latency_seconds:p50_rate1m", "p50"), t("slo:latency_seconds:p95_rate1m", "p95"), t("slo:latency_seconds:p99_rate1m", "p99")], "s",
    { defaults: { min: 0 }, ...lines([{ color: "transparent", value: null }, { color: "red", value: 0.25 }]), description: "Red line: the 250 ms p99 objective. Quantiles are interpolated from histogram buckets; the SLI itself reads the 25/100/250 ms bucket boundaries exactly." }),
  ts("Availability burn rate (x budget)", 0, 12, 12, 8, [
    t('slo:burn_rate:rate5m{slo="ledger-post-availability"}', "5m"), t('slo:burn_rate:rate30m{slo="ledger-post-availability"}', "30m"),
    t('slo:burn_rate:rate1h{slo="ledger-post-availability"}', "1h"), t('slo:burn_rate:rate6h{slo="ledger-post-availability"}', "6h")], "none",
    { ...lines([{ color: "transparent", value: null }, { color: "orange", value: 6 }, { color: "red", value: 14.4 }]), description: "Page when 1h AND 5m exceed 14.4x, or 6h AND 30m exceed 6x (Google SRE Workbook, ch. 5)." }),
  ts("Latency p99 SLO burn rate (x budget)", 12, 12, 12, 8, [
    t('slo:burn_rate:rate5m{slo="ledger-post-latency-p99"}', "5m"), t('slo:burn_rate:rate30m{slo="ledger-post-latency-p99"}', "30m"),
    t('slo:burn_rate:rate1h{slo="ledger-post-latency-p99"}', "1h"), t('slo:burn_rate:rate6h{slo="ledger-post-latency-p99"}', "6h")], "none",
    lines([{ color: "transparent", value: null }, { color: "orange", value: 6 }, { color: "red", value: 14.4 }])),
  { id: id++, type: "state-timeline", title: "Alerts", datasource: ds, gridPos: { x: 0, y: 20, w: 24, h: 5 },
    targets: [t('ALERTS{alertstate="firing"}', "{{alertname}}")],
    options: { showValue: "never", mergeValues: true, rowHeight: 0.8 },
    fieldConfig: { defaults: { color: { mode: "fixed", fixedColor: "red" }, mappings: [{ type: "value", options: { "1": { text: "firing", color: "red" } } }] }, overrides: [] } },
  ts("Database connection pool (Npgsql)", 0, 25, 12, 8, [
    t(`sum by (db_client_connection_state) (db_client_connection_count{${POOL}})`, "{{db_client_connection_state}}"), t(`sum(db_client_connection_max{${POOL}})`, "max"),
    t(`sum(db_client_connection_pending_requests{${POOL}}) or vector(0)`, "waiting for a connection")], "none"),
  ts("Bytes to and from PostgreSQL (request path)", 0, 33, 12, 7, [
    t(`sum(rate(db_client_operation_npgsql_bytes_written_total{${POOL}}[30s]))`, "written (queries)"),
    t(`sum(rate(db_client_operation_npgsql_bytes_read_total{${POOL}}[30s]))`, "read (results)")], "Bps"),
  ts("Posting attempts and outcomes", 12, 25, 12, 8, [
    t("sum by (result) (rate(ledger_posting_attempts_total[30s]))", "attempt {{result}}"),
    t("sum by (outcome) (rate(ledger_posting_outcomes_total[30s]))", "outcome {{outcome}}")], "ops"),
];
// Grafana requires a unique refId per query within a panel.
for (const p of panels) p.targets.forEach((q, i) => { q.refId = String.fromCharCode(65 + i); });

const dashboard = {
  uid: "slo-ledger", title: "Ledger posting SLOs", tags: ["slo", "chaos"], timezone: "utc", schemaVersion: 39, version: 1,
  refresh: "5s", time: { from: "now-15m", to: "now" }, editable: false, graphTooltip: 1, panels,
  annotations: { list: [] }, templating: { list: [] },
  description: "Generated by tools/dashboard/build-dashboard.mjs (see docs/slo.md). Availability and latency SLOs for POST /api/v1/journal-entries with multiwindow burn rates.",
};
process.stdout.write(JSON.stringify(dashboard, null, 2) + "\n");
