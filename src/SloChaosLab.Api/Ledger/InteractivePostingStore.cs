// Adapted from hekimcan206/ledger-recon (src/LedgerRecon.Api/Ledger/PostingService.cs), same author, MIT licence.
// The SERIALIZABLE variant and its retry loop were dropped: this lab keeps the row-lock strategy only, and retries
// moved to the resilience pipeline where they cover network faults as well as serialization failures.
using System.Data;
using Npgsql;
using NpgsqlTypes;
using SloChaosLab.Ledger;

namespace SloChaosLab.Api.Ledger;

/// <summary>
/// Posts a journal entry exactly once per idempotency key, driving the transaction from the client:
/// <list type="number">
/// <item><b>Claim the key</b> with <c>INSERT ... ON CONFLICT (idempotency_key) DO NOTHING</c>; a concurrent request
/// with the same key blocks on the uncommitted index entry and then replays.</item>
/// <item><b>Lock the accounts</b> with <c>SELECT ... FOR UPDATE</c> in ascending id order (no deadlocks).</item>
/// <item><b>Check rules on the locked rows</b>, then update balances and insert postings in one batch.</item>
/// <item><b>Commit</b>; a deferred constraint trigger re-checks that debits equal credits.</item>
/// </list>
/// That is four network round trips per entry, and the account row locks are held across three of them. Experiment
/// 1 measures what that costs when the network between the service and the database slows down.
/// </summary>
internal sealed class InteractivePostingStore(NpgsqlDataSource dataSource, TimeProvider timeProvider) : IPostingStore
{
    public async Task<Result<PostOutcome>> PostAsync(string key, byte[] hash, JournalEntryDraft draft, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        // 1. Claim the idempotency key.
        var entryId = Guid.CreateVersion7(timeProvider.GetUtcNow());
        DateTimeOffset postedAt;
        await using (var claim = new NpgsqlCommand("""
            INSERT INTO ledger.journal_entries (id, idempotency_key, request_hash, description, value_date, currency, external_ref)
            VALUES ($1, $2, $3, $4, $5, $6, $7)
            ON CONFLICT (idempotency_key) DO NOTHING
            RETURNING posted_at
            """, connection, tx))
        {
            claim.Parameters.Add(new() { Value = entryId });
            claim.Parameters.Add(new() { Value = key });
            claim.Parameters.Add(new() { Value = hash });
            claim.Parameters.Add(new() { Value = draft.Description });
            claim.Parameters.Add(new() { Value = draft.ValueDate });
            claim.Parameters.Add(new() { Value = draft.Currency });
            claim.Parameters.Add(new() { Value = (object?)draft.ExternalRef ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            var inserted = await claim.ExecuteScalarAsync(cancellationToken);
            if (inserted is null)
            {
                return await PostingQueries.ReplayAsync(connection, tx, key, hash, cancellationToken);
            }

            postedAt = new DateTimeOffset(DateTime.SpecifyKind((DateTime)inserted, DateTimeKind.Utc));
        }

        // 2. Lock the touched accounts in a fixed order.
        var codes = draft.AccountCodes;
        var accounts = new Dictionary<string, LockedAccount>(StringComparer.Ordinal);
        await using (var select = new NpgsqlCommand(
            "SELECT id, code, type, currency, prevent_overdraft, balance_minor FROM ledger.accounts WHERE code = ANY($1) ORDER BY id FOR UPDATE",
            connection, tx))
        {
            select.Parameters.Add(new() { Value = codes.ToArray() });
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var account = new LockedAccount(
                    reader.GetInt64(0), reader.GetString(1), AccountTypes.FromDbValue(reader.GetString(2)), reader.GetString(3), reader.GetBoolean(4), reader.GetInt64(5));
                accounts[account.Code] = account;
            }
        }

        // 3. Rules that need current state. Every early return rolls back, releasing the idempotency claim.
        var missing = codes.Where(c => !accounts.ContainsKey(c)).ToArray();
        if (missing.Length > 0)
        {
            return LedgerError.Unprocessable("entry.unknown_account", $"Unknown account(s): {string.Join(", ", missing)}.");
        }

        var wrongCurrency = accounts.Values.Where(a => a.Currency != draft.Currency).Select(a => a.Code).ToArray();
        if (wrongCurrency.Length > 0)
        {
            return LedgerError.Unprocessable(
                "entry.currency_mismatch", $"Entry currency is {draft.Currency}; account(s) {string.Join(", ", wrongCurrency)} use another currency.");
        }

        var deltas = draft.NetDeltas();
        foreach (var (code, delta) in deltas)
        {
            var account = accounts[code];
            var after = account.BalanceMinor + delta;
            if (account.PreventOverdraft && account.Type.NormalBalance(after) < 0)
            {
                return LedgerError.Unprocessable(
                    "account.insufficient_funds",
                    $"Account {code} would go to {Money.Format(account.Type.NormalBalance(after))} {draft.Currency}; overdraft is not allowed.");
            }
        }

        // 4. Apply. Both statements go in one round trip.
        var touched = deltas.Where(d => d.Value != 0).OrderBy(d => accounts[d.Key].Id).ToArray();
        await using (var batch = new NpgsqlBatch(connection, tx))
        {
            var update = new NpgsqlBatchCommand("""
                UPDATE ledger.accounts AS a
                   SET balance_minor = a.balance_minor + d.delta, version = a.version + 1, updated_at = now()
                  FROM unnest($1::bigint[], $2::bigint[]) AS d(id, delta)
                 WHERE a.id = d.id
                """);
            update.Parameters.Add(new() { Value = touched.Select(d => accounts[d.Key].Id).ToArray() });
            update.Parameters.Add(new() { Value = touched.Select(d => d.Value).ToArray() });

            var insert = new NpgsqlBatchCommand("""
                INSERT INTO ledger.postings (entry_id, line_no, account_id, currency, direction, amount_minor)
                SELECT $1, l.line_no, l.account_id, $2, l.direction, l.amount
                  FROM unnest($3::smallint[], $4::bigint[], $5::text[], $6::bigint[]) AS l(line_no, account_id, direction, amount)
                """);
            insert.Parameters.Add(new() { Value = entryId });
            insert.Parameters.Add(new() { Value = draft.Currency });
            insert.Parameters.Add(new() { Value = Enumerable.Range(1, draft.Lines.Count).Select(i => (short)i).ToArray() });
            insert.Parameters.Add(new() { Value = draft.Lines.Select(l => accounts[l.AccountCode].Id).ToArray() });
            insert.Parameters.Add(new() { Value = draft.Lines.Select(l => l.Direction == Direction.Debit ? "D" : "C").ToArray() });
            insert.Parameters.Add(new() { Value = draft.Lines.Select(l => l.AmountMinor).ToArray() });

            batch.BatchCommands.Add(update);
            batch.BatchCommands.Add(insert);
            await batch.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return Result.Success(new PostOutcome(PostingQueries.ToView(entryId, key, draft, postedAt), Replayed: false));
    }

    private sealed record LockedAccount(long Id, string Code, AccountType Type, string Currency, bool PreventOverdraft, long BalanceMinor);
}
