# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses [Semantic Versioning](https://semver.org/).

## [1.0.0] - 2026-11-12

### Added

- Ledger posting service under test, vendored from hekimcan206/ledger-recon (attributed per file): exactly-once
  posting with idempotency keys, ordered row locks, balanced-entry and append-only invariants in PostgreSQL 17,
  least-privilege roles, 1,000 synthetic accounts.
- OpenTelemetry traces, metrics and logs; traces and logs to the Aspire dashboard, metrics pushed over OTLP to
  Prometheus' native receiver.
- SLOs for `POST /api/v1/journal-entries`: availability 99.9 %, latency p50 25 ms / p95 100 ms / p99 250 ms (as ratio
  SLOs over 30 days), defined in code, with generated recording rules and multiwindow, multi-burn-rate alerts
  (Google SRE Workbook: 14.4x 1h/5m, 6x 6h/30m, 1x 3d/6h); promtool unit tests and a golden test.
- Grafana SLO dashboard as code (burn rates, budget, latency quantiles, alert timeline, pool and link metrics).
- Chaos runner: fixed-rate open-model NBomber load, Toxiproxy fault schedules (latency, jitter, bandwidth, connection
  reset bursts, black-hole partition), SLO read-back from Prometheus, client- versus server-side comparison,
  error-budget accounting and a steady-state alert model; capacity steps; CI smoke mode.
- Fixes motivated by the experiments, behind the `hardened` lab profile: single-round-trip posting function, request
  deadline shorter than the caller's timeout, jittered retries made safe by idempotency, circuit breaker, load
  shedding, tight Npgsql timeouts and pool sizing.
- Five blameless postmortems with before/after numbers and dashboard screenshots; measured baseline and capacity.
- `docker compose up` lab with a demo that partitions the database and waits for the page; Aspire AppHost for local
  development; chiseled non-root images.
- Unit, property, golden and Testcontainers integration tests (PostgreSQL + Toxiproxy, including fault injection).
- CI: build, format, vulnerable-package audit, tests with coverage, promtool rule tests, dashboard drift check,
  container images, compose chaos smoke test that asserts the burn-rate alert fires; CodeQL.

[1.0.0]: https://github.com/hekimcan206/slo-chaos-lab/releases/tag/v1.0.0
