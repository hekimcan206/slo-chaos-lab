# Threat model

Scope: the lab as shipped (`docker compose up` on a developer machine), the ledger service, and the CI chaos job.
All data is synthetic. The lab is not meant to be exposed beyond localhost; this document says what would have to
change if parts of it were.

## Assets

| Asset | Why it matters |
|---|---|
| Ledger integrity (balances, postings, idempotency keys) | The service under test is a ledger; a fix that breaks exactly-once posting is worse than the outage it prevents |
| SLO signal (metrics, rules, alerts) | A wrong SLI or a silenced alert hides real outages; that is a failure mode this lab studies |
| Toxiproxy control plane | Whoever reaches it can cut or slow the database link |
| Credentials | Database role passwords and the Grafana admin password (local defaults) |
| CI runner | Executes compose and the chaos runner on every push |

## Actors

- A developer or reviewer running the lab locally (trusted).
- Another process or user on the same machine or LAN (untrusted).
- A contributor opening a pull request (semi-trusted; CI runs their code without secrets).

## Threats (STRIDE) and mitigations

| # | Threat | Category | Mitigation | Residual risk |
|---|---|---|---|---|
| 1 | A retry posts the same entry twice after an ambiguous commit (connection reset during COMMIT) | Tampering (integrity) | Every posting carries an idempotency key; the unique index arbitrates; retries replay. Integration test asserts exactly one entry after a reset burst | None known |
| 2 | The single-round-trip function bypasses an invariant that the interactive path had | Tampering | Same statements in the same order; deferred balance trigger still fires; parity tests run both modes on the same cases; `search_path` pinned, names qualified | Logic now in two places until the interactive path is retired |
| 3 | A function or role grants more than needed | Elevation of privilege | EXECUTE on `ledger.post_entry` revoked from PUBLIC, granted to `ledger_app` only; app role has no UPDATE/DELETE on history and no DDL (test asserts DELETE is refused) | - |
| 4 | Someone on the network injects faults or writes fake metrics | Tampering / DoS | Toxiproxy admin, Prometheus (OTLP write endpoint), Grafana and the Aspire dashboard bind to 127.0.0.1 in compose | Anyone with local access can still do it; acceptable for a lab |
| 5 | Fake metrics push makes alerts fire or stay silent | Spoofing | Same as 4; the receiver has no authentication. In production: OTLP over TLS with auth, or a collector in between | Lab-only |
| 6 | An SLI that does not count what callers experience (abandoned requests are 499, not 5xx) | Repudiation of an outage | Experiment 5 documents it; the hardened deadline (1.5 s) is shorter than the caller timeout (2 s) and a unit test pins that order | Callers with shorter timeouts than 1.5 s would reopen the gap; a client- or LB-side SLI would close it |
| 7 | Retry storm amplifies an outage | DoS | Bounded retries with jitter inside a 1.5 s deadline; circuit breaker; concurrency limit of 64 with no queue | - |
| 8 | Secrets leak via logs or telemetry | Information disclosure | Request bodies not logged; the lab endpoint reports settings, not connection strings; the Npgsql pool is named so metrics do not carry the connection string; no secrets in the repo (`.env.example`) | Compose defaults are weak by design; override them before any shared use |
| 9 | A malicious PR abuses CI | Elevation / supply chain | Workflows have `contents: read`; no secrets are used; images and packages are pinned; CodeQL and vulnerable-package audit on every push | Standard GitHub-hosted runner risk |
| 10 | Chaos tooling left active after a run | DoS | The runner resets Toxiproxy (`POST /reset`) at the start and at the end of every run, including on failure (`finally`) | A killed runner process can leave a toxic; `curl -X POST localhost:58474/reset` |

## Out of scope

Authentication and authorisation on the API, TLS, multi-tenant isolation and alert routing (no Alertmanager). They
are listed under Limitations in the README.
