# 6. Experiment method: fixed-rate open-model load, Toxiproxy on the DB link, fresh Prometheus per run

- Status: accepted
- Date: 2026-11-11

## Context

Before/after numbers are only comparable if the runs differ in nothing but the service configuration under test.

## Decision

- **Load**: NBomber `Inject` at a fixed 50 requests/s, an open model: new requests arrive on schedule however many are
  in flight. A closed model (N looping workers) slows down with the service and hides exactly the queueing the
  experiments are about (coordinated omission). Callers time out at 2 s. Every request is a synthetic transfer between
  two of 1,000 accounts with a fresh idempotency key. 500 untimed requests warm JIT and pools before the clock starts.
- **Shape**: 60 s warm-up, 120 s fault, 90 s recovery, for every experiment and variant.
- **Fault location**: Toxiproxy between the service and PostgreSQL only; migrations bypass it.
- **Where the load runs**: in a container on the compose network, so no request crosses Docker Desktop's port
  forwarding. A first trial from the Windows host lost the Docker engine under connection churn.
- **Two vantage points**: client-observed numbers from the runner (every request, exact nearest-rank percentiles) and
  the server-side SLI read back from Prometheus (counter deltas at phase boundaries, accurate to the 1 s push
  interval). Where they disagree, that is a result, not noise (experiment 5).
- **Fresh Prometheus per run**: the API and Prometheus are recreated before every run (TSDB on tmpfs), so no run
  inherits another's burn-rate windows. The bias this introduces: the 1 h and 6 h windows contain only the run, so a
  fault looks proportionally bigger than on a service with an hour of healthy history, and the *measured* time to page
  is optimistic. The runner therefore also replays the alert expressions over the client-observed events, assuming a
  healthy history at the same rate (`AlertModel`): the **steady-state** time to page. Both are reported.
- **Error budget consumed** = bad events / (budget fraction x rate x 30 days). At 50 req/s the 99.9 % SLO allows
  129,600 failed posts in 30 days; one second of total outage costs 0.039 % of that.

## Consequences

- Each run takes about 5 minutes; the suite (6 experiments x 2 variants) about an hour.
- The numbers come from one loaded laptop (environment in docs/benchmarks.md). They show relative effects, not
  capacity-planning figures.

## Sources

- NBomber load simulations: https://nbomber.com/docs/nbomber/load-simulation
- Toxiproxy toxics: https://github.com/Shopify/toxiproxy#toxics
- Gil Tene, "How NOT to Measure Latency" (coordinated omission): https://www.youtube.com/watch?v=lJ8ydIuPFeU
