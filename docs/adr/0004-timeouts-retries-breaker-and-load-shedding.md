# 4. A request deadline shorter than the caller's, jittered retries, a circuit breaker and load shedding

- Status: accepted
- Date: 2026-11-11

## Context

The baseline service runs on Npgsql defaults: 15 s to connect or to wait for a pooled connection, 30 s per command,
100 connections, no deadline, no retries. Experiments 3 to 5 (bandwidth cap, connection resets, partition) show what
that means under a fault; the postmortems have the numbers. Callers in the lab give up after 2 s.

## Decision

The `hardened` lab profile (one environment variable, `Lab__Profile`) wraps each posting in a Polly v8 pipeline,
outermost first:

1. **Request deadline, 1.5 s.** Shorter than the caller's 2 s on purpose: the service must answer (503) while the
   caller is still listening. Otherwise the failure is recorded as 499 "client closed request", which the availability
   SLI does not count, and the outage is invisible to the alerts (experiment 5).
2. **Retry**, up to 3 times, exponential from 50 ms with jitter, on transient database errors
   (`NpgsqlException.IsTransient`: socket errors, timeouts, serialization failures) and on attempt timeouts. Retrying a
   write is safe only because a posting is idempotent: a retry after an ambiguous commit replays the stored entry
   instead of posting twice (integration test `Retries_ride_out_a_short_reset_burst_and_post_exactly_once`).
3. **Circuit breaker**: opens when at least half of 20 or more attempts in 10 s fail, stays open 5 s, then probes.
   While open, requests fail at once with 503 and `Retry-After` instead of queueing on a dead link.
4. **Attempt timeout, 1 s.**

Client-side database settings: `Timeout=2` (connect and pool wait), `Command Timeout=2`, `Cancellation Timeout=-1`
(on cancellation, break the connection instead of sending a cancel request over a second connection that would hang
like the first), `Maximum Pool Size=32`. **Load shedding**: an ASP.NET Core concurrency limiter admits 64 postings in
flight with no queue; excess requests get 503 immediately rather than waiting to miss their deadline.

Failures map to 503 problem details with stable codes (`dependency.timeout`, `dependency.circuit_open`,
`dependency.unavailable`), so callers can tell "retry with the same key" from a bug (500).

## Options considered

- Only lowering the Npgsql timeouts: bounds a single statement, not the request; no protection against retry storms.
- Retries without jitter: synchronised retries after a reset burst hit the database in waves.
- Microsoft.Extensions.Http.Resilience: built for outgoing HttpClient calls; the dependency here is a database, so
  Polly.Core is used directly with the same strategy types.
- A queue in front of the limiter: in an overload, queued requests only wait to fail later and inflate tail latency.

## Consequences

- During a total outage the service fails fast and visibly, which burns the availability budget honestly and pages.
- Retries add attempts (counted in `ledger.posting.attempts`), bounded by the deadline and the breaker.
- Deadlines must stay strictly ordered (attempt < request deadline < caller timeout); a unit test pins the order.

## Sources

- Google SRE book, "Handling Overload": https://sre.google/sre-book/handling-overload/
- AWS Builders' Library, "Timeouts, retries, and backoff with jitter": https://aws.amazon.com/builders-library/timeouts-retries-and-backoff-with-jitter/
- Polly v8 documentation: https://www.pollydocs.org/
- Npgsql connection string parameters: https://www.npgsql.org/doc/connection-string-parameters.html
