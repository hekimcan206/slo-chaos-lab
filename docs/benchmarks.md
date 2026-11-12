# Measurements

Everything here was measured on the machine below and is reproducible with the commands at the end. Raw results:
one JSON file per run in [results/](results/) and the generated [results/summary.md](results/summary.md), which also
holds the full per-phase tables (client-observed and server-side) for every run.

## Environment

| | |
|---|---|
| Host | Windows 11 Home laptop, Intel Core i7-13620H (10 cores, 16 logical CPUs), 16 GB RAM |
| Containers | Docker Desktop 29.9.1, WSL2 VM with 16 CPUs and 7.6 GB RAM, kernel 6.18 |
| Host load | Shared: another project's stack (Kafka-compatible broker, Debezium, search engine, several PostgreSQL instances) ran on the same Docker VM throughout; free host RAM was often below 1 GB |
| Service | .NET 10.0.15, chiseled ASP.NET runtime image, Release build; PostgreSQL 17 (alpine) with data on tmpfs; Toxiproxy 2.12.0 |
| Load generator | NBomber 6.7.1 inside a container on the compose network (no host port forwarding), open-model `Inject`, fresh Idempotency-Key per request, 2 s caller timeout, 500 untimed warm-up requests before each run |
| Telemetry | OpenTelemetry .NET 1.20, metrics pushed every 1 s to Prometheus 3.15.1 (rules every 5 s); Grafana 13.2.6 |

## Method

See [ADR 0006](adr/0006-experiment-method.md). Each run: 60 s warm-up, 120 s fault, 90 s recovery, 50 posts/s. The API
and Prometheus are recreated before every run. Client-observed percentiles are exact (nearest rank over every
request in the phase); server-side figures are Prometheus counter deltas at phase boundaries (1 s push interval, 2 s
read-back offset), so a request is counted in the phase in which it *completed*. Each variant ran once; every
fault-phase figure is over about 6,000 requests.

## Baseline latency and throughput

No fault, 50 posts/s, client-observed. Each run has three windows (60 s, 120 s, 90 s); the table gives the per-window
values and their median.

| Profile | Window | p50 ms | p95 ms | p99 ms | Errors |
|---|---|---:|---:|---:|---:|
| baseline (interactive) | 60 s | 5.4 | 10.9 | 16.7 | 0 |
| | 120 s | 5.0 | 103 | 823 | 110 timeouts (host stall, see below) |
| | 90 s | 4.5 | 9.1 | 13.4 | 0 |
| | **median** | **5.0** | **10.9** | **16.7** | |
| hardened (function) | 60 s | 4.4 | 11.6 | 136 | 0 |
| | 120 s | 4.1 | 7.6 | 15.9 | 0 |
| | 90 s | 3.9 | 7.5 | 17.4 | 0 |
| | **median** | **4.1** | **7.6** | **17.4** | |

On a healthy local link the two posting modes are within noise of each other: a local round trip costs a fraction of
a millisecond, so removing three of them barely shows. The difference appears only when the link degrades
(experiments 1-3).

**Host stall.** In the baseline profile's middle window, with no fault injected, 110 requests timed out at 2 s and the
slowest took 7.4 s, all within a few seconds. Nothing in the service changed; the machine was shared and memory was
tight, and an earlier attempt to run the suite had lost the Docker engine entirely. The window is reported, not
dropped; the median is unaffected. It is a reminder why the experiments compare fault phases run back to back, not
single numbers against a target.

Throughput (no fault, offered-rate steps of 45 s, first 10 s of each step discarded):

Baseline profile (interactive posting, Npgsql defaults):

| offered req/s | achieved 2xx/s | errors | p50 ms | p95 ms | p99 ms | max ms |
|---:|---:|---:|---:|---:|---:|---:|
| 50 | 49.7 | 61 | 6.9 | 644 | 1829 | 3035 |
| 100 | 100.0 | 0 | 5.3 | 8.7 | 12.0 | 42.9 |
| 200 | 200.0 | 0 | 5.0 | 9.6 | 95.4 | 181 |
| 400 | 400.0 | 0 | 6.2 | 24.9 | 61.2 | 172 |
| 800 | 800.0 | 0 | 10.7 | 209 | 338 | 536 |

Hardened profile (posting function, pool 32, concurrency limit 64):

| offered req/s | achieved 2xx/s | errors | p50 ms | p95 ms | p99 ms | max ms |
|---:|---:|---:|---:|---:|---:|---:|
| 50 | 50.1 | 0 | 4.4 | 8.3 | 10.9 | 15.9 |
| 100 | 100.0 | 0 | 4.1 | 7.1 | 36.7 | 657 |
| 200 | 198.4 | 57 | 4.3 | 21.6 | 360 | 653 |
| 400 | 400.0 | 0 | 4.6 | 15.5 | 28.6 | 79.8 |
| 800 | 799.7 | 9 | 5.3 | 11.4 | 23.3 | 39.1 |

Both profiles sustained every offered rate up to 800 posts/s (the highest step tried). At 800/s the hardened service kept p99 at 23 ms while the interactive one reached 338 ms, breaching the 250 ms objective: fewer round trips also mean shorter lock hold times and less work per post. Two steps were hit by host stalls (baseline at 50/s: 61 timeouts; hardened at 200/s: 57 errors), the same kind of event as in the baseline window above; each step ran once, so treat single steps with care. 800/s was not exceeded because the load generator and the service share the same loaded machine.

## Experiments

Fault-phase summary (client-observed unless noted); details and screenshots in the postmortems.

| Experiment | Variant | Availability | p50 / p95 / p99 ms | Within 250 ms | Budget: availability | Budget: p99 | Max burn 5 m (avail. / p99) |
|---|---|---:|---|---:|---:|---:|---:|
| latency +100 ms | before | 100 % | 408 / 450 / 606 | 0.4 % | 0 % | 0.461 % | 0x / 66.4x |
| | after | 100 % | 105 / 109 / 166 | 99.7 % | 0 % | 0.001 % | 0x / 0.5x |
| jitter 40 +/- 40 ms | before | 100 % | 184 / 256 / 288 | 93.18 % | 0 % | 0.032 % | 0x / 4.3x |
| | after | 100 % | 59.7 / 81.5 / 86.0 | 99.93 % | 0 % | 0 % | 0x / 0.1x |
| bandwidth 4 KB/s | before | 100 % | 418 / 449 / 615 | 0.32 % | 0 % | 0.461 % | 0x / 66.4x |
| | after | 100 % | 127 / 130 / 220 | 99.97 % | 0 % | 0 % | 0x / 0.1x |
| reset bursts | before | 96.8 % | 4.4 / 9.7 / 56.4 | 100 % | 0.148 % | 0 % | 21.5x / 0x |
| | after | 99.47 % | 4.1 / 10.7 / 138 | 99.83 % | 0.025 % | 0.001 % | 3.4x / 0.2x |
| partition | before | 1.52 % (SLI: 100 %) | timeouts at 2,001 ms | - | 4.56 % (SLI: 0 %) | 0.007 % | 0x / 63.1x |
| | after | 0 % (SLI: 2.9 %) | failures 0.96 ms p50 | - | 4.83 % (SLI: 4.69 %) | 0 % | 652x / 5.3x |

Time to page (`LedgerPostAvailabilityFastBurn`) during the partition: measured 10 s after the fault started in the run's
own Prometheus (hardened); 52 s in the steady-state model (54 s for the baseline profile, from client-observed
events, whereas the baseline's server-side SLI never paged).

## Reproduce

```bash
docker compose up -d postgres toxiproxy api prometheus grafana
sh scripts/run-experiments.sh                       # all experiments, both variants (about an hour)
docker compose run --rm --no-deps -v "$PWD/docs/results:/results" demo capacity \
  --rates 50,100,200,400,800 --step 45 --api http://api:8080/ --out /results
dotnet run --project tools/SloChaosLab.Chaos -- report --results docs/results
```
