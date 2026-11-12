# 1. Vendor a small copy of the ledger-recon posting API as the service under test

- Status: accepted
- Date: 2026-11-11

## Context

The lab measures how a service behaves against its SLOs when the network to its database misbehaves. It needs a
service whose request path is realistic (a real transaction, row locks, an idempotency guarantee, invariants in the
database), not an echo endpoint whose latency is all framework. Building a new product is out of scope.

[ledger-recon](https://github.com/hekimcan206/ledger-recon) (same author) already has such a path: `POST
/api/v1/journal-entries` claims an idempotency key, locks the touched accounts in id order, checks rules and applies
balances in one PostgreSQL transaction, with a deferred trigger that re-checks debits = credits at commit.

## Options considered

1. **Run ledger-recon as is** (its image, its compose). Couples the lab to that repo's release cadence, drags in the
   reconciliation subsystem and its file parsers, and every fix the experiments motivate would have to land there.
2. **Vendor the posting path only** (domain rules, schema migration, the interactive posting transaction), attributed
   file by file, and change it here.
3. **Write a toy service.** Its latency profile would not resemble a real write path: no locks, no multi-statement
   transaction, nothing for a network fault to amplify.

## Decision

Option 2. Copied files carry an "Adapted from hekimcan206/ledger-recon (path), same author, MIT licence" header:
`src/SloChaosLab.Ledger/*` (balanced-entry rules, integer money, request fingerprint), the `0001_ledger.sql` migration,
the migration runner, the role init script, problem details, health endpoints, and the interactive posting transaction
(`InteractivePostingStore`). The SERIALIZABLE variant and reconciliation were left out. A synthetic chart of 1,000
customer accounts (`0002_synthetic_accounts.sql`) gives the load generator transfers that never fail a business rule,
so every 5xx in a run is caused by the fault under test.

## Consequences

- The "before" runs measure ledger-recon's real protocol, so the postmortem findings apply to it.
- Fixes land here first (`0003_post_entry_function.sql`, the resilience pipeline); porting them back is a follow-up.
- Two copies of the domain rules exist. They are small and pure, and both repos test them.
