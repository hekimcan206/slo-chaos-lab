using Npgsql;
using NpgsqlTypes;
using SloChaosLab.Ledger;

namespace SloChaosLab.Api.Ledger;

/// <summary>
/// Posts through <c>ledger.post_entry(...)</c> in autocommit mode: one network round trip per entry instead of four,
/// and no row lock is ever held while a packet is in flight. Same idempotency, ordering and validation semantics
/// as <see cref="InteractivePostingStore"/>; an integration test runs both against the same cases.
/// </summary>
internal sealed class FunctionPostingStore(NpgsqlDataSource dataSource, TimeProvider timeProvider) : IPostingStore
{
    /// <summary>SQLSTATE raised by the function for business-rule rejections (rolls back the claim).</summary>
    public const string RuleViolationSqlState = "LR422";

    public async Task<Result<PostOutcome>> PostAsync(string key, byte[] hash, JournalEntryDraft draft, CancellationToken cancellationToken)
    {
        var entryId = Guid.CreateVersion7(timeProvider.GetUtcNow());
        await using var cmd = dataSource.CreateCommand(
            "SELECT entry_id, posted_at, replayed FROM ledger.post_entry($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)");
        cmd.Parameters.Add(new() { Value = entryId });
        cmd.Parameters.Add(new() { Value = key });
        cmd.Parameters.Add(new() { Value = hash });
        cmd.Parameters.Add(new() { Value = draft.Description });
        cmd.Parameters.Add(new() { Value = draft.ValueDate });
        cmd.Parameters.Add(new() { Value = draft.Currency, NpgsqlDbType = NpgsqlDbType.Char });
        cmd.Parameters.Add(new() { Value = (object?)draft.ExternalRef ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new() { Value = draft.Lines.Select(l => l.AccountCode).ToArray() });
        cmd.Parameters.Add(new() { Value = draft.Lines.Select(l => l.Direction == Direction.Debit ? "D" : "C").ToArray() });
        cmd.Parameters.Add(new() { Value = draft.Lines.Select(l => l.AmountMinor).ToArray() });

        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            var id = reader.GetGuid(0);
            var postedAt = reader.GetFieldValue<DateTimeOffset>(1);
            var replayed = reader.GetBoolean(2);
            await reader.CloseAsync();

            if (!replayed)
            {
                return Result.Success(new PostOutcome(PostingQueries.ToView(id, key, draft, postedAt), Replayed: false));
            }

            // Replays are rare (a retried request); reading the stored lines back costs a second round trip only then.
            var stored = await PostingQueries.GetAsync(dataSource, id, cancellationToken);
            return Result.Success(new PostOutcome(stored!, Replayed: true));
        }
        catch (PostgresException ex) when (ex.SqlState == RuleViolationSqlState)
        {
            return ex.MessageText == "idempotency.key_reused"
                ? PostingQueries.KeyReused()
                : LedgerError.Unprocessable(ex.MessageText, ex.Hint ?? ex.MessageText);
        }
    }
}
