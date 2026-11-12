# Security policy

## Supported versions

Only the latest release receives fixes.

## Reporting a vulnerability

Please **do not open a public issue**. Use GitHub's private vulnerability reporting instead:
**Security → Report a vulnerability** on this repository. You can expect an acknowledgement within
5 working days; if the report is confirmed, a fix or mitigation plan follows within 30 days.

Please include the affected version or commit, steps to reproduce and the impact you observed.

## Security-relevant design

See [docs/threat-model.md](docs/threat-model.md) for assets, threats and mitigations. In short:

| Area | Default |
|---|---|
| Ledger integrity | Balanced entries, append-only history and idempotency are enforced by PostgreSQL itself (constraints, deferred triggers, privileges), not only by the application; the posting function keeps every one of them |
| Database access | The API runs as `ledger_app` (no UPDATE/DELETE on history, no DDL, EXECUTE only on `ledger.post_entry`); migrations run as `ledger_migrator` on a separate, direct connection |
| Fault injection | The Toxiproxy admin API can break the database link at will; compose binds it to 127.0.0.1 only |
| Observability | Prometheus' OTLP receiver accepts writes without authentication; Grafana allows anonymous viewers and the Aspire dashboard runs unsecured. All are bound to 127.0.0.1 and are local-lab conveniences |
| Logs and metrics | Request bodies are never logged; connection strings are not echoed (the `/api/v1/lab` endpoint reports settings, not credentials) and the Npgsql pool name on metrics is a label (`request-path`), not the connection string |
| Container | Chiseled runtime images for the API and the chaos runner: no shell or package manager, non-root UID 1654 |
| Secrets | None in the repository; compose defaults are local-lab values and can be overridden via `.env` (see `.env.example`) |
| Supply chain | Centrally pinned package and image versions, updated by hand; CI fails on known-vulnerable direct or transitive NuGet packages and runs `npm audit` for the screenshot tool; CodeQL `security-and-quality` on every push |

**Not included:** authentication and authorisation on the API, TLS, alert routing. This repository is a lab with
synthetic data and must not be exposed as-is.
