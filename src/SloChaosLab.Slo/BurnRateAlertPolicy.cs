namespace SloChaosLab.Slo;

/// <param name="LongWindow">Window that decides whether enough budget has been burned to matter.</param>
/// <param name="ShortWindow">One twelfth of the long window; stops the alert as soon as the burn stops.</param>
/// <param name="BurnRate">Multiple of the sustainable error rate that triggers the tier.</param>
/// <param name="BudgetConsumed">Fraction of the period's budget spent by the time the long window alone would fire.</param>
public sealed record BurnRateTier(string Name, TimeSpan LongWindow, TimeSpan ShortWindow, double BurnRate, double BudgetConsumed, string Severity);

/// <summary>
/// The multiwindow, multi-burn-rate policy recommended in the Google SRE Workbook, chapter 5 "Alerting on SLOs"
/// (https://sre.google/workbook/alerting-on-slos/), for a 30-day SLO period. A tier fires only when the burn rate
/// exceeds its threshold over both the long and the short window: the long window gives precision (enough budget is
/// really gone), the short window gives a fast reset (the alert clears minutes after the fault is fixed).
/// The thresholds follow from <c>burn rate = budget consumed x period / long window</c>:
/// 2 % of 30 days in 1 h is 14.4, 5 % in 6 h is 6, 10 % in 3 days is 1.
/// </summary>
public static class BurnRateAlertPolicy
{
    public static IReadOnlyList<BurnRateTier> Tiers { get; } =
    [
        new("FastBurn", TimeSpan.FromHours(1), TimeSpan.FromMinutes(5), 14.4, 0.02, "page"),
        new("SlowBurn", TimeSpan.FromHours(6), TimeSpan.FromMinutes(30), 6, 0.05, "page"),
        new("BudgetErosion", TimeSpan.FromDays(3), TimeSpan.FromHours(6), 1, 0.10, "ticket"),
    ];

    /// <summary>Every window a recording rule is needed for, shortest first.</summary>
    public static IReadOnlyList<TimeSpan> Windows { get; } =
        [.. Tiers.SelectMany(t => new[] { t.ShortWindow, t.LongWindow }).Distinct().Order()];
}
