# 5. Post an entry in one round trip with a PL/pgSQL function

- Status: accepted
- Date: 2026-11-11

## Context

The vendored posting transaction is driven from the client: INSERT ... ON CONFLICT (claim the idempotency key),
SELECT ... FOR UPDATE (lock the accounts in id order), an UPDATE + INSERT batch, COMMIT. Npgsql folds BEGIN and the
pool reset into the first statement, so that is four network round trips, with the account row locks held across
three of them. Experiment 1 (+100 ms on every database response) measured the consequence: every millisecond on the
link is paid about four times per request, and the latency SLO collapses.

## Decision

Move the same statements, in the same order, into `ledger.post_entry(...)` (migration 0003) and call it in autocommit
mode: one round trip, and no lock is ever held while a packet is in flight. Business-rule rejections raise SQLSTATE
`LR422` (message = stable error code, hint = text; Npgsql redacts `DETAIL` but not `HINT`), which rolls back the whole
statement, idempotency claim included, exactly like an early return in the interactive version. The function runs
with `search_path = pg_catalog, pg_temp` and fully qualified names; EXECUTE is revoked from PUBLIC and granted to the
application role only. The deferred balance trigger still fires at commit.

`Ledger__PostingMode` selects `Interactive` (baseline) or `Function` (hardened). An integration suite runs the same
cases against both modes: posting, replay, key reuse, unknown account (and reusing the key after the rejection),
20 concurrent requests with one key, and the global ledger invariants.

## Options considered

- Pipelining the interactive statements: the lock SELECT's result decides whether to apply, so the statements cannot
  all be sent before the first answer arrives.
- Moving the database closer: the right answer where possible, but not a code fix, and the point stands for any link
  that degrades.
- A stored procedure with explicit transaction control: unnecessary; a function call in autocommit is one atomic
  statement.

## Consequences

- Latency added on the link is paid once per posting instead of about four times.
- Some logic now lives in SQL; it is versioned as a migration and covered by the parity tests.
- A replay needs a second query to return the stored lines; replays are rare (retries).
