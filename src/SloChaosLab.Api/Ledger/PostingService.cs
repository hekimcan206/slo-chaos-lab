using Npgsql;
using SloChaosLab.Api.Infrastructure;
using SloChaosLab.Ledger;

namespace SloChaosLab.Api.Ledger;

/// <summary>Fingerprints the request, runs one posting attempt and records the outcome.</summary>
internal sealed class PostingService(IPostingStore store, LedgerMetrics metrics)
{
    public async Task<Result<PostOutcome>> PostAsync(string idempotencyKey, JournalEntryDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var hash = RequestFingerprint.Compute(draft);
        try
        {
            var result = await store.PostAsync(idempotencyKey, hash, draft, cancellationToken);
            metrics.Attempt("ok");
            metrics.Outcome(!result.IsSuccess ? "rejected" : result.Value.Replayed ? "replayed" : "posted");
            return result;
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            metrics.Attempt(ex is NpgsqlException { IsTransient: true } ? "transient_error" : "error");
            metrics.Outcome("failed");
            throw;
        }
    }
}
