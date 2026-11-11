using SloChaosLab.Ledger;

namespace SloChaosLab.Api.Ledger;

internal sealed record EntryLineView(int LineNo, string AccountCode, Direction Direction, long AmountMinor);

internal sealed record EntryView(
    Guid Id,
    string IdempotencyKey,
    string Description,
    DateOnly ValueDate,
    string Currency,
    string? ExternalRef,
    DateTimeOffset PostedAt,
    IReadOnlyList<EntryLineView> Lines);

/// <param name="Replayed">True when the key had already been used with the same payload and nothing new was written.</param>
internal sealed record PostOutcome(EntryView Entry, bool Replayed);

internal sealed record AccountView(string Code, string Name, string Type, string Currency, long BalanceMinor, long Version);

/// <summary>
/// One attempt at posting an entry against the database. Implementations must be safe to call again with the same
/// key after any failure, including one where the commit outcome is unknown: the idempotency key turns a repeated
/// attempt into a replay, which is what makes client and server retries safe (docs/adr/0005).
/// </summary>
internal interface IPostingStore
{
    Task<Result<PostOutcome>> PostAsync(string idempotencyKey, byte[] requestHash, JournalEntryDraft draft, CancellationToken cancellationToken);
}

/// <summary>How the posting transaction talks to PostgreSQL (docs/adr/0005).</summary>
internal enum PostingMode
{
    /// <summary>The ledger-recon protocol: claim, lock, check, apply, commit from the client. Four round trips.</summary>
    Interactive,

    /// <summary>The same steps inside one PL/pgSQL function call in autocommit mode. One round trip.</summary>
    Function,
}

internal sealed class LedgerOptions
{
    public const string Section = "Ledger";

    public PostingMode PostingMode { get; set; } = PostingMode.Interactive;
}
