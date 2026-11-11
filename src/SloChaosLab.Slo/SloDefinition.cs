namespace SloChaosLab.Slo;

public enum SliKind
{
    /// <summary>Good event: the request did not fail with a 5xx.</summary>
    Availability,

    /// <summary>Good event: a request that did not fail completed within <see cref="SloDefinition.ThresholdSeconds"/>.</summary>
    Latency,
}

/// <summary>
/// A request-based SLO: over <see cref="Period"/>, at least <see cref="Objective"/> of the eligible requests are good.
/// A percentile target ("p95 under 100 ms") is the same statement as "95 % of requests under 100 ms", so latency
/// percentiles are expressed as ratio SLOs too. That is what makes an error budget and a burn rate defined for them.
/// </summary>
/// <param name="Name">Stable identifier, used as the <c>slo</c> label on every recorded series and alert.</param>
/// <param name="Objective">Target fraction of good events, e.g. 0.999.</param>
/// <param name="ThresholdSeconds">Latency threshold; must be a histogram bucket boundary so the SLI is exact.</param>
public sealed record SloDefinition(
    string Name,
    string Description,
    SliKind Kind,
    double Objective,
    TimeSpan Period,
    double? ThresholdSeconds = null)
{
    /// <summary>The error budget as a fraction of events: 1 - objective.</summary>
    public double ErrorBudget => 1 - Objective;

    /// <summary>
    /// Whether the multiwindow, multi-burn-rate alerts can fire at all. The fastest tier needs an error ratio of
    /// 14.4 x budget; once that exceeds 100 % (budgets above ~6.9 %, e.g. a p50 objective) the alert is unreachable,
    /// so such SLOs are recorded and charted but not alerted on.
    /// </summary>
    public bool IsAlertable => BurnRateAlertPolicy.Tiers.Max(t => t.BurnRate) * ErrorBudget <= 1;

    /// <summary>PascalCase prefix for alert names, e.g. ledger-post-latency-p99 becomes LedgerPostLatencyP99.</summary>
    public string AlertPrefix => string.Concat(Name.Split('-').Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
}
