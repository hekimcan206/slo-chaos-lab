# Running the experiments

Method and its trade-offs: [ADR 0006](adr/0006-experiment-method.md). Results: [results/summary.md](results/summary.md)
(generated) and one postmortem per experiment in [postmortems/](postmortems/).

## The five faults

All faults sit on the link between the ledger service and PostgreSQL (Toxiproxy proxy `postgres`), at a fixed
50 requests/s: 60 s warm-up, 120 s fault, 90 s recovery.

| # | Experiment | Toxiproxy | Models |
|---|---|---|---|
| 1 | `latency` | `latency` downstream, 100 ms | a cross-zone hop, a congested link, a slow proxy |
| 2 | `jitter` | `latency` downstream, 40 ms +/- 40 ms | the same link with a wide spread |
| 3 | `bandwidth` | `bandwidth` upstream, 4 KB/s per connection | a saturated or throttled link |
| 4 | `reset` | `reset_peer` upstream, on for 500 ms every 10 s | proxy or load-balancer restarts, NAT flushes, failovers |
| 5 | `partition` | `timeout` (0 = black hole) up- and downstream | a real partition: packets dropped, nothing refused |

Each experiment runs twice: `before` on the `baseline` profile (the service as vendored from ledger-recon, Npgsql
defaults) and `after` on the `hardened` profile (all fixes). `baseline` is a no-fault run of each profile.

## Reproduce

```bash
docker compose up -d postgres toxiproxy api prometheus grafana   # the lab, without the demo container
sh scripts/run-experiments.sh                                    # all experiments (about an hour)
sh scripts/run-experiments.sh partition                          # or one
```

For each run the script recreates the API (with `LAB_PROFILE=baseline` or `hardened`) and Prometheus (fresh TSDB),
runs the chaos runner in a container on the compose network, writes `docs/results/<experiment>-<variant>.json`, and
captures the Grafana dashboard for the run's time range into `docs/images/<experiment>-<variant>.png`. At the end it
regenerates `docs/results/summary.md`. Screenshots use the pinned Playwright image by default; with
`CHROME=/path/to/chrome` they use a local headless Chrome instead (the final baseline and latency runs were captured
that way, after the Playwright image had been removed from the shared machine to free disk space).

One run by hand:

```bash
docker compose run --rm --no-deps -v "$PWD/docs/results:/results" demo run \
  --experiment latency --variant after --rate 50 --warmup 60 --fault 120 --recovery 90 \
  --api http://api:8080/ --toxiproxy http://toxiproxy:8474/ --prometheus http://prometheus:9090/ --out /results
```

Baseline throughput (closed steps of fixed rate, no fault):

```bash
docker compose run --rm --no-deps -v "$PWD/docs/results:/results" demo capacity \
  --rates 50,100,200,400,800 --step 45 --api http://api:8080/ --out /results
```

## Reading a result

- **Client-observed**: what callers lived through. Timeouts are requests the 2 s caller gave up on.
- **Server-side SLI**: what Prometheus, and therefore the alerts, saw. A request counts when it *completes*, so a
  request that hangs past a phase lands in the next one, and a request its caller abandoned is a 499, not a 5xx.
- **Budget consumed**: fraction of a 30-day error budget at 50 req/s used by the whole run.
- **Fired after fault start (measured)**: from the run's own Prometheus, whose long windows hold only the run, so
  optimistic. **Steady-state model**: the same alert expression replayed over the client-observed events with one
  healthy hour before the run, i.e. what a long-running service would have done.
