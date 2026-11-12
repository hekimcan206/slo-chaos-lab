using Microsoft.Extensions.Options;
using Npgsql;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;
using SloChaosLab.Api.Infrastructure;
using SloChaosLab.Ledger;

namespace SloChaosLab.Api.Ledger;

/// <summary>
/// Fingerprints the request and runs the posting through the resilience pipeline (or straight through, in the
/// baseline profile). Pipeline, outermost first (docs/adr/0004):
/// <list type="number">
/// <item><b>Request deadline</b> (1.5 s): the service answers before its caller's 2 s timeout, always.</item>
/// <item><b>Retry</b> on transient database errors and slow attempts: exponential backoff with jitter. Safe only
/// because a posting is idempotent: a retry after an ambiguous commit replays instead of posting twice.</item>
/// <item><b>Circuit breaker</b>: when most recent attempts fail, stop sending work to a database that cannot take
/// it; answer 503 immediately and probe again after a pause.</item>
/// <item><b>Attempt timeout</b> (1 s): one hung statement cannot eat the whole deadline.</item>
/// </list>
/// </summary>
internal sealed class PostingService
{
    private readonly IPostingStore _store;
    private readonly LedgerMetrics _metrics;
    private readonly ResiliencePipeline _pipeline;

    public PostingService(IPostingStore store, LedgerMetrics metrics, IOptions<ResilienceOptions> options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        _store = store;
        _metrics = metrics;
        _pipeline = options.Value.Enabled ? Build(options.Value, metrics, timeProvider) : ResiliencePipeline.Empty;
    }

    public async Task<Result<PostOutcome>> PostAsync(string idempotencyKey, JournalEntryDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var hash = RequestFingerprint.Compute(draft);
        try
        {
            var result = await _pipeline.ExecuteAsync(
                static async (state, ct) => await state.Self.AttemptAsync(state.Key, state.Hash, state.Draft, ct),
                (Self: this, Key: idempotencyKey, Hash: hash, Draft: draft),
                cancellationToken);
            _metrics.Outcome(!result.IsSuccess ? "rejected" : result.Value.Replayed ? "replayed" : "posted");
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller went away (HTTP 499); the database may or may not have committed. A retry by the caller
            // with the same key resolves it either way.
            _metrics.Outcome("abandoned");
            throw;
        }
        catch (BrokenCircuitException)
        {
            _metrics.Outcome("short_circuited");
            throw;
        }
        catch (Exception ex) when (IsDependencyFailure(ex))
        {
            _metrics.Outcome("failed");
            throw;
        }
    }

    private async Task<Result<PostOutcome>> AttemptAsync(string key, byte[] hash, JournalEntryDraft draft, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _store.PostAsync(key, hash, draft, cancellationToken);
            _metrics.Attempt("ok");
            return result;
        }
        catch (Exception ex) when (IsDependencyFailure(ex))
        {
            _metrics.Attempt(ex switch
            {
                NpgsqlException { IsTransient: true } => "transient_error",
                OperationCanceledException or TimeoutException => "timeout",
                _ => "error",
            });
            throw;
        }
    }

    internal static bool IsDependencyFailure(Exception ex) =>
        ex is NpgsqlException or TimeoutException or TimeoutRejectedException or OperationCanceledException;

    private static ResiliencePipeline Build(ResilienceOptions o, LedgerMetrics metrics, TimeProvider timeProvider)
    {
        // Transient = the database or the network failed, not the request. PostgresException covers SQLSTATEs the
        // server marks transient (serialization failure, deadlock, admin shutdown); socket errors and Npgsql timeouts
        // arrive as NpgsqlException with IsTransient set. Business-rule rejections are values, never exceptions.
        var transient = new PredicateBuilder()
            .Handle<NpgsqlException>(e => e.IsTransient)
            .Handle<TimeoutRejectedException>();

        return new ResiliencePipelineBuilder { TimeProvider = timeProvider }
            .AddTimeout(TimeSpan.FromMilliseconds(o.RequestDeadlineMs))
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = transient,
                MaxRetryAttempts = o.MaxRetryAttempts,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromMilliseconds(o.RetryBaseDelayMs),
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                ShouldHandle = transient,
                FailureRatio = o.BreakerFailureRatio,
                MinimumThroughput = o.BreakerMinimumThroughput,
                SamplingDuration = TimeSpan.FromSeconds(o.BreakerSamplingSeconds),
                BreakDuration = TimeSpan.FromSeconds(o.BreakerBreakSeconds),
                OnOpened = _ =>
                {
                    metrics.Breaker("opened");
                    return default;
                },
                OnClosed = _ =>
                {
                    metrics.Breaker("closed");
                    return default;
                },
            })
            .AddTimeout(TimeSpan.FromMilliseconds(o.AttemptTimeoutMs))
            .Build();
    }
}
