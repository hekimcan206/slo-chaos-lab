// The request/response contract and validation are adapted from hekimcan206/ledger-recon
// (src/LedgerRecon.Api/Features/JournalEntryEndpoints.cs), same author, MIT licence.
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using SloChaosLab.Api.Infrastructure;
using SloChaosLab.Api.Ledger;
using SloChaosLab.Ledger;

namespace SloChaosLab.Api.Features;

internal sealed record PostingLineRequest(string? AccountCode, string? Direction, decimal Amount);

internal sealed record PostEntryRequest(
    string? Description, DateOnly? ValueDate, string? Currency, string? ExternalRef, IReadOnlyList<PostingLineRequest>? Lines);

internal sealed record EntryLineResponse(int LineNo, string AccountCode, string Direction, decimal Amount);

internal sealed record EntryResponse(
    Guid Id, string IdempotencyKey, string Description, DateOnly ValueDate, string Currency, string? ExternalRef, DateTimeOffset PostedAt,
    IReadOnlyList<EntryLineResponse> Lines)
{
    public static EntryResponse From(EntryView e) => new(
        e.Id, e.IdempotencyKey, e.Description, e.ValueDate, e.Currency, e.ExternalRef, e.PostedAt,
        [.. e.Lines.Select(l => new EntryLineResponse(l.LineNo, l.AccountCode, l.Direction == Direction.Debit ? "debit" : "credit", Money.ToMajor(l.AmountMinor)))]);
}

internal sealed record AccountResponse(string Code, string Name, string Type, string Currency, decimal Balance, long Version);

internal static class LedgerEndpoints
{
    /// <summary>The SLO-bearing route. The SLI selects on exactly this http.route value.</summary>
    public const string PostRoute = "/api/v1/journal-entries";
    public const string IdempotencyKeyHeader = "Idempotency-Key";
    public const string ReplayedHeader = "Idempotent-Replayed";
    private const int MaxKeyLength = 128;

    public const string PostingLimiter = "posting";

    public static IEndpointRouteBuilder MapLedgerEndpoints(this IEndpointRouteBuilder app, bool shedLoad = false)
    {
        var post = app.MapPost(PostRoute, PostAsync)
            .WithTags("Journal entries")
            .WithSummary("Post a balanced journal entry exactly once")
            .WithDescription(
                "Requires an Idempotency-Key header. Repeating a request with the same key and payload returns the original entry " +
                "(header Idempotent-Replayed: true) without writing anything. The same key with a different payload is rejected with 422.");
        if (shedLoad)
        {
            post.RequireRateLimiting(PostingLimiter);
        }

        app.MapGet($"{PostRoute}/{{id:guid}}", GetEntryAsync).WithTags("Journal entries").WithSummary("Get a journal entry with its postings");
        app.MapGet("/api/v1/accounts/{code}", GetAccountAsync).WithTags("Accounts").WithSummary("Get an account and its balance");
        return app;
    }

    private static async Task<Results<Created<EntryResponse>, ProblemHttpResult>> PostAsync(
        [FromHeader(Name = IdempotencyKeyHeader)] string? idempotencyKey,
        PostEntryRequest request,
        PostingService postings,
        TimeProvider clock,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        var key = NormaliseKey(idempotencyKey);
        if (key is null)
        {
            return Problems.Validation(
                "idempotency.key_missing",
                $"An {IdempotencyKeyHeader} header of 1-{MaxKeyLength} visible ASCII characters is required to post an entry.",
                IdempotencyKeyHeader);
        }

        var lines = new List<PostingLine>(request.Lines?.Count ?? 0);
        for (var i = 0; i < (request.Lines?.Count ?? 0); i++)
        {
            var line = request.Lines![i];
            Direction direction;
            if (string.Equals(line.Direction, "debit", StringComparison.OrdinalIgnoreCase))
            {
                direction = Direction.Debit;
            }
            else if (string.Equals(line.Direction, "credit", StringComparison.OrdinalIgnoreCase))
            {
                direction = Direction.Credit;
            }
            else
            {
                return Problems.Validation("entry.direction_invalid", "Direction must be 'debit' or 'credit'.", $"lines[{i}].direction");
            }

            if (!Money.TryToMinor(line.Amount, out var minor))
            {
                return Problems.Validation("entry.amount_invalid", "Amounts have at most two decimals.", $"lines[{i}].amount");
            }

            lines.Add(new PostingLine(line.AccountCode ?? string.Empty, direction, minor));
        }

        var valueDate = request.ValueDate ?? DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var draft = JournalEntryDraft.Create(request.Description, valueDate, request.Currency, request.ExternalRef, lines);
        if (!draft.IsSuccess)
        {
            return Problems.From(draft.Error);
        }

        var result = await postings.PostAsync(key, draft.Value, cancellationToken);
        if (!result.IsSuccess)
        {
            return Problems.From(result.Error);
        }

        // A replay answers exactly like the original request did (201 with the same entry), so a client that lost
        // the first response cannot tell the difference, except through this header.
        http.Response.Headers[ReplayedHeader] = result.Value.Replayed ? "true" : "false";
        return TypedResults.Created($"{PostRoute}/{result.Value.Entry.Id}", EntryResponse.From(result.Value.Entry));
    }

    private static async Task<Results<Ok<EntryResponse>, NotFound>> GetEntryAsync(Guid id, NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        var entry = await PostingQueries.GetAsync(dataSource, id, cancellationToken);
        return entry is null ? TypedResults.NotFound() : TypedResults.Ok(EntryResponse.From(entry));
    }

    private static async Task<Results<Ok<AccountResponse>, NotFound>> GetAccountAsync(string code, NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        var normalised = JournalEntryDraft.NormaliseAccountCode(code);
        var account = normalised is null ? null : await PostingQueries.GetAccountAsync(dataSource, normalised, cancellationToken);
        return account is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(new AccountResponse(account.Code, account.Name, account.Type, account.Currency, Money.ToMajor(account.BalanceMinor), account.Version));
    }

    /// <summary>Accepts the bare key or the structured-field string form ("...") used by the IETF Idempotency-Key draft.</summary>
    internal static string? NormaliseKey(string? key)
    {
        key = key?.Trim();
        if (key is { Length: >= 2 } && key[0] == '"' && key[^1] == '"')
        {
            key = key[1..^1];
        }

        return key is { Length: > 0 and <= MaxKeyLength } && key.All(c => c is >= '!' and <= '~') ? key : null;
    }
}
