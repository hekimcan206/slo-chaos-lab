# 3. Request-based SLOs and multiwindow, multi-burn-rate alerts generated from code

- Status: accepted
- Date: 2026-11-11

## Context

The posting endpoint needs an availability SLO and latency SLOs at p50, p95 and p99, an error budget for each, and
alerts that page when the budget is really at risk and stay quiet otherwise.

## Decision

**SLIs** come from the OpenTelemetry HTTP server metric `http.server.request.duration` (route
`/api/v1/journal-entries`, method POST), as received by Prometheus:
- availability: good = not 5xx. 4xx are the caller's problem and do not burn the budget;
- latency: good = a non-5xx request completed within the threshold, read from the histogram bucket whose upper
  bound *is* the threshold (25 ms, 100 ms and 250 ms are default ASP.NET Core boundaries), so no interpolation.

**Objectives** over a 30-day period: availability 99.9 %; latency 50 % within 25 ms, 95 % within 100 ms, 99 % within
250 ms. A percentile target is the same statement as a ratio ("p99 <= 250 ms" = "99 % of requests within 250 ms"),
which is what makes an error budget and a burn rate defined for latency at all. The thresholds were checked against
the measured baseline (docs/benchmarks.md), so a breach means something changed.

**Alerts** follow the Google SRE Workbook, chapter 5 ("Alerting on SLOs"), recommended policy: page when the burn rate
exceeds 14.4 over 1 h *and* 5 m (2 % of the budget in an hour), page at 6 over 6 h and 30 m (5 % in six hours), ticket
at 1 over 3 d and 6 h (10 % in three days). The thresholds follow from `burn = budget consumed x period / long window`;
a unit test re-derives them. The short window is 1/12 of the long one: it does not make the alert fire later, it makes
it *stop* minutes after the burn stops. There is no `for:` clause, as in the workbook.

**p50 is charted, not alerted.** The fastest tier needs an error ratio of 14.4 x budget; for a 50 % budget that is
720 %, unreachable. The generator skips any SLO where 14.4 x budget > 1 and says so in the rules file.

**Rules are generated** from `SloCatalog` (`chaos generate-rules`), in the spirit of Sloth and Pyrra but small. A golden
test fails when the committed YAML and the catalog disagree, and promtool unit tests (`deploy/prometheus/tests`) prove
on synthetic series that: 2 % errors pages on the fast tier; 0.05 % errors never pages; a 15-minute outage pages and
then clears once the 5 m window is clean while the 1 h burn rate is still above 300x; 0.8 % errors pages only on the
slow tier; 20 % slow requests page the p99 SLO but not the p95 one.

## Options considered

- Threshold alerts on error rate or p99 ("p99 > 250 ms for 5 m"): either noisy or slow, and blind to the budget.
- Single-window burn-rate alerts: a 1 h window resets slowly after recovery; a 5 m window pages on blips.
- Sloth or Pyrra: good tools, but another binary or operator for four SLOs; the generator here is about 150 lines and
  tested.

## Consequences

- One source of truth for SLO numbers, shared by the rules, the runner's budget arithmetic and the docs.
- The SLI is server-side. Experiment 5 shows the blind spot this creates (requests the caller abandoned are recorded
  as 499, not 5xx) and how the service-side fix closes it. A load-balancer or client-side SLI is the next step.

## Sources

- Google SRE Workbook, "Alerting on SLOs": https://sre.google/workbook/alerting-on-slos/
- Google SRE Workbook, "Implementing SLOs": https://sre.google/workbook/implementing-slos/
- Grafana Labs, multi-window multi-burn-rate alerts: https://grafana.com/blog/how-to-implement-multi-window-multi-burn-rate-alerts-with-grafana-cloud/
