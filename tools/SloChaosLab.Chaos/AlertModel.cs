using SloChaosLab.Slo;

namespace SloChaosLab.Chaos;

/// <summary>
/// Replays the multiwindow burn-rate alert expressions over the client-observed request stream, assuming the
/// service had been healthy at the same request rate before the run started.
/// <para>
/// Why: each run starts with a fresh Prometheus, so its 1 h and 6 h windows hold only the few minutes of the run.
/// A short history makes the long-window ratio larger than it would be on a service with an hour of healthy traffic
/// behind it, so the measured time-to-detect is optimistic. This model gives the steady-state answer: the time
/// at which the same alert would fire on a long-running service, from the same events.
/// </para>
/// </summary>
internal static class AlertModel
{
    /// <summary>Seconds after <paramref name="faultStartSeconds"/> at which the tier would first fire; null if never.</summary>
    public static double? FirstFiring(
        IReadOnlyList<RequestSample> samples, SloDefinition slo, BurnRateTier tier, double ratePerSecond, double faultStartSeconds, double runEndSeconds)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(slo);
        ArgumentNullException.ThrowIfNull(tier);
        for (var t = faultStartSeconds; t <= runEndSeconds; t += 1)
        {
            var longBurn = Burn(samples, slo, ratePerSecond, t, tier.LongWindow.TotalSeconds);
            var shortBurn = Burn(samples, slo, ratePerSecond, t, tier.ShortWindow.TotalSeconds);
            if (longBurn > tier.BurnRate && shortBurn > tier.BurnRate)
            {
                return t - faultStartSeconds;
            }
        }

        return null;
    }

    /// <summary>
    /// Burn rate over the window ending at <paramref name="at"/>. Events are attributed to the moment the request
    /// completed (start + latency), as a server-side counter would see them. Time before the run counts as healthy
    /// traffic at <paramref name="ratePerSecond"/>.
    /// </summary>
    public static double Burn(IReadOnlyList<RequestSample> samples, SloDefinition slo, double ratePerSecond, double at, double windowSeconds)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(slo);
        var from = at - windowSeconds;
        long bad = 0, eligible = 0;
        foreach (var s in samples)
        {
            var completed = s.StartSeconds + (s.LatencyMs / 1000);
            if (completed < from || completed >= at)
            {
                continue;
            }

            if (slo.Kind == SliKind.Availability)
            {
                eligible++;
                bad += s.IsAvailable ? 0 : 1;
            }
            else if (s.IsSuccess)
            {
                eligible++;
                bad += s.LatencyMs > slo.ThresholdSeconds!.Value * 1000 ? 1 : 0;
            }
        }

        // Healthy history before the run (t < 0) fills the rest of the window.
        if (from < 0)
        {
            eligible += (long)Math.Round(-from * ratePerSecond);
        }

        return eligible == 0 ? 0 : SloMath.BurnRate((double)bad / eligible, slo.Objective);
    }
}
