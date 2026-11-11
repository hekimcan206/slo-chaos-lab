using SloChaosLab.Slo;

namespace SloChaosLab.Chaos;

internal sealed record Phase(string Name, double FromSeconds, double ToSeconds)
{
    public double DurationSeconds => ToSeconds - FromSeconds;
}

/// <summary>Client-observed numbers for one phase of a run.</summary>
/// <param name="P50Ms">Latency percentiles of successful (2xx) requests, nearest-rank.</param>
/// <param name="P99AllMs">p99 over every request, failures and timeouts included: what the slowest callers lived through.</param>
/// <param name="LatencyCompliance">Per latency SLO: fraction of successful requests within its threshold.</param>
internal sealed record PhaseStats(
    string Phase,
    double DurationSeconds,
    int Requests,
    int Succeeded,
    int ServerErrors,
    int ClientTimeouts,
    int TransportErrors,
    double Availability,
    double ThroughputPerSecond,
    double P50Ms,
    double P95Ms,
    double P99Ms,
    double P99AllMs,
    double MaxMs,
    IReadOnlyDictionary<string, double> LatencyCompliance);

internal static class Analysis
{
    /// <summary>Nearest-rank percentile (no interpolation): the reported value is a latency some request really had.</summary>
    public static double Percentile(IReadOnlyList<double> sortedAscending, double percentile)
    {
        ArgumentNullException.ThrowIfNull(sortedAscending);
        if (percentile is <= 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(percentile), percentile, "Percentile must be in (0, 100].");
        }

        if (sortedAscending.Count == 0)
        {
            return double.NaN;
        }

        var rank = (int)Math.Ceiling(percentile / 100 * sortedAscending.Count);
        return sortedAscending[Math.Clamp(rank, 1, sortedAscending.Count) - 1];
    }

    public static PhaseStats Summarise(Phase phase, IEnumerable<RequestSample> all)
    {
        ArgumentNullException.ThrowIfNull(phase);
        var samples = all.Where(s => s.StartSeconds >= phase.FromSeconds && s.StartSeconds < phase.ToSeconds).ToList();
        var ok = samples.Where(s => s.IsSuccess).Select(s => s.LatencyMs).Order().ToList();
        var every = samples.Select(s => s.LatencyMs).Order().ToList();

        var compliance = SloCatalog.All
            .Where(s => s.Kind == SliKind.Latency)
            .ToDictionary(
                s => s.Name,
                s => SloMath.Compliance(ok.Count(ms => ms <= s.ThresholdSeconds!.Value * 1000), ok.Count));

        return new PhaseStats(
            phase.Name,
            phase.DurationSeconds,
            samples.Count,
            ok.Count,
            samples.Count(s => s.Status >= 500),
            samples.Count(s => s.Status == RequestSample.ClientTimeout),
            samples.Count(s => s.Status == RequestSample.TransportError),
            SloMath.Compliance(samples.Count(s => s.IsAvailable), samples.Count),
            phase.DurationSeconds > 0 ? ok.Count / phase.DurationSeconds : 0,
            Percentile(ok, 50),
            Percentile(ok, 95),
            Percentile(ok, 99),
            Percentile(every, 99),
            every.Count == 0 ? double.NaN : every[^1],
            compliance);
    }

    /// <summary>
    /// Error budget consumed by a run, client-observed, for every SLO: bad events over the budget a full 30-day
    /// period would have at the same fixed request rate. Latency SLOs count successful-but-slow requests.
    /// </summary>
    public static IReadOnlyDictionary<string, double> BudgetConsumed(IReadOnlyCollection<RequestSample> samples, double ratePerSecond)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var slo in SloCatalog.All)
        {
            long bad = slo.Kind == SliKind.Availability
                ? samples.Count(s => !s.IsAvailable)
                : samples.Count(s => s.IsSuccess && s.LatencyMs > slo.ThresholdSeconds!.Value * 1000);
            result[slo.Name] = SloMath.BudgetConsumed(bad, ratePerSecond, slo.Objective, slo.Period);
        }

        return result;
    }

    /// <summary>
    /// Seconds from the end of the fault until the service is healthy again: the start of the first 10 s window in
    /// which every request succeeded within the p99 threshold. Null when it never recovered during the run.
    /// </summary>
    public static double? RecoverySeconds(IReadOnlyList<RequestSample> samples, double faultEndSeconds, double runEndSeconds)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var thresholdMs = SloCatalog.LatencyP99.ThresholdSeconds!.Value * 1000;
        for (var t = faultEndSeconds; t + 10 <= runEndSeconds; t += 1)
        {
            var window = samples.Where(s => s.StartSeconds >= t && s.StartSeconds < t + 10).ToList();
            if (window.Count > 0 && window.All(s => s.IsSuccess && s.LatencyMs <= thresholdMs))
            {
                return t - faultEndSeconds;
            }
        }

        return null;
    }
}
