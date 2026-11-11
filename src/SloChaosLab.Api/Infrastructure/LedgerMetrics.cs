using System.Diagnostics.Metrics;

namespace SloChaosLab.Api.Infrastructure;

/// <summary>
/// Domain counters next to the HTTP SLI. The SLO itself is computed from http.server.request.duration (what the
/// caller experiences); these explain it: how many database attempts failed, were retried or short-circuited.
/// </summary>
internal sealed class LedgerMetrics : IDisposable
{
    public const string MeterName = "SloChaosLab.Ledger";

    private readonly Meter _meter;
    private readonly Counter<long> _outcomes;
    private readonly Counter<long> _attempts;
    private readonly Counter<long> _breaker;

    public LedgerMetrics(IMeterFactory meterFactory)
    {
        _meter = meterFactory.Create(MeterName);
        _outcomes = _meter.CreateCounter<long>("ledger.posting.outcomes", "{request}", "Posting requests by final outcome.");
        _attempts = _meter.CreateCounter<long>("ledger.posting.attempts", "{attempt}", "Database attempts by result, including retries.");
        _breaker = _meter.CreateCounter<long>("ledger.circuit_breaker.transitions", "{transition}", "Circuit breaker state changes.");
    }

    /// <summary>posted, replayed, rejected, failed, abandoned, shed or short_circuited.</summary>
    public void Outcome(string outcome) => _outcomes.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    /// <summary>ok, transient_error, timeout or error.</summary>
    public void Attempt(string result) => _attempts.Add(1, new KeyValuePair<string, object?>("result", result));

    /// <summary>opened or closed.</summary>
    public void Breaker(string transition) => _breaker.Add(1, new KeyValuePair<string, object?>("transition", transition));

    public void Dispose() => _meter.Dispose();
}
