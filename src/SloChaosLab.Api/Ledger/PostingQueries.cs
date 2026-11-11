using Npgsql;
using SloChaosLab.Ledger;

namespace SloChaosLab.Api.Ledger;

/// <summary>Read paths shared by both posting modes.</summary>
internal static class PostingQueries
{
    public static EntryView ToView(Guid id, string key, JournalEntryDraft draft, DateTimeOffset postedAt) => new(
        id, key, draft.Description, draft.ValueDate, draft.Currency, draft.ExternalRef, postedAt,
        [.. draft.Lines.Select((l, i) => new EntryLineView(i + 1, l.AccountCode, l.Direction, l.AmountMinor))]);

    /// <summary>The key is taken: same payload replays the stored entry, a different payload is rejected (422).</summary>
    public static async Task<Result<PostOutcome>> ReplayAsync(
        NpgsqlConnection connection, NpgsqlTransaction? tx, string key, byte[] hash, CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand("SELECT id, request_hash FROM ledger.journal_entries WHERE idempotency_key = $1", connection, tx);
        cmd.Parameters.Add(new() { Value = key });
        Guid id;
        byte[] storedHash;
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Idempotency key conflicted but no entry is visible.");
            }

            id = reader.GetGuid(0);
            storedHash = (byte[])reader[1];
        }

        if (!storedHash.AsSpan().SequenceEqual(hash))
        {
            return KeyReused();
        }

        var entry = await LoadAsync(connection, tx, id, cancellationToken);
        return Result.Success(new PostOutcome(entry!, Replayed: true));
    }

    public static LedgerError KeyReused() => LedgerError.Unprocessable(
        "idempotency.key_reused", "This Idempotency-Key was already used with a different request payload.");

    public static async Task<EntryView?> GetAsync(NpgsqlDataSource dataSource, Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await LoadAsync(connection, null, id, cancellationToken);
    }

    public static async Task<AccountView?> GetAccountAsync(NpgsqlDataSource dataSource, string code, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand(
            "SELECT code, name, type, currency, balance_minor, version FROM ledger.accounts WHERE code = $1");
        cmd.Parameters.Add(new() { Value = code });
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new AccountView(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4), reader.GetInt64(5))
            : null;
    }

    private static async Task<EntryView?> LoadAsync(NpgsqlConnection connection, NpgsqlTransaction? tx, Guid id, CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT e.id, e.idempotency_key, e.description, e.value_date, e.currency, e.external_ref, e.posted_at,
                   p.line_no, a.code, p.direction, p.amount_minor
              FROM ledger.journal_entries e
              JOIN ledger.postings p ON p.entry_id = e.id
              JOIN ledger.accounts a ON a.id = p.account_id
             WHERE e.id = $1
             ORDER BY p.line_no
            """, connection, tx);
        cmd.Parameters.Add(new() { Value = id });
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        EntryView? entry = null;
        var lines = new List<EntryLineView>();
        while (await reader.ReadAsync(cancellationToken))
        {
            entry ??= new EntryView(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetFieldValue<DateOnly>(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetFieldValue<DateTimeOffset>(6), lines);
            lines.Add(new EntryLineView(reader.GetInt16(7), reader.GetString(8), reader.GetString(9) == "D" ? Direction.Debit : Direction.Credit, reader.GetInt64(10)));
        }

        return entry;
    }
}
