using System.Globalization;
using System.Text;

namespace SloChaosLab.Slo;

/// <summary>
/// Generates the Prometheus recording and alerting rules from <see cref="SloCatalog"/>, in the spirit of Sloth and
/// Pyrra but small enough to read in one sitting. The SLO is defined once, in code; the YAML is an output that a
/// golden test keeps in sync, and promtool unit tests (deploy/prometheus/tests) prove the alerts fire when they should.
/// </summary>
public static class PrometheusRuleGenerator
{
    public const string ErrorRatioRecord = "slo:sli_error:ratio_rate";
    public const string BurnRateRecord = "slo:burn_rate:rate";
    public const string BudgetRemainingRecord = "slo:error_budget_remaining:ratio";
    public const string ObjectiveRecord = "slo:objective:ratio";

    /// <summary>Quantiles charted on the dashboard; they explain an SLO breach but are not the SLI.</summary>
    private static readonly double[] ChartedQuantiles = [0.5, 0.95, 0.99];

    public static string Generate(IReadOnlyList<SloDefinition> slos)
    {
        ArgumentNullException.ThrowIfNull(slos);
        var sb = new StringBuilder();
        sb.Append("""
            # GENERATED FILE. Do not edit by hand: change src/SloChaosLab.Slo/SloCatalog.cs and run
            #   dotnet run --project tools/SloChaosLab.Chaos -- generate-rules
            # A golden test fails when this file and the catalog disagree; deploy/prometheus/tests holds promtool tests.
            #
            # Multiwindow, multi-burn-rate alerts: Google SRE Workbook, ch. 5 "Alerting on SLOs"
            # (https://sre.google/workbook/alerting-on-slos/). An alert fires only when both its long and its short
            # window burn faster than the threshold. No `for:` clause: the short window already filters blips, and a
            # `for:` would only delay detection.
            groups:

            """);

        var windows = BurnRateAlertPolicy.Windows.Append(SloCatalog.Period).ToArray();
        foreach (var slo in slos)
        {
            sb.Append(CultureInfo.InvariantCulture, $"  - name: slo-{slo.Name}\n");
            sb.Append("    rules:\n");
            Record(sb, ObjectiveRecord, $"vector({Number(slo.Objective)})", slo.Name);

            foreach (var window in windows)
            {
                Record(sb, ErrorRatioRecord + Duration(window), ErrorRatioExpression(slo, Duration(window)), slo.Name);
            }

            foreach (var window in windows)
            {
                Record(sb, BurnRateRecord + Duration(window),
                    $"{ErrorRatioRecord}{Duration(window)}{{slo=\"{slo.Name}\"}} / {Number(slo.ErrorBudget)}", slo.Name);
            }

            Record(sb, BudgetRemainingRecord,
                $"1 - {BurnRateRecord}{Duration(SloCatalog.Period)}{{slo=\"{slo.Name}\"}}", slo.Name);

            if (!slo.IsAlertable)
            {
                sb.Append(CultureInfo.InvariantCulture,
                    $"    # No burn-rate alerts: a {Number(slo.ErrorBudget * 100)}% budget cannot be burned at 14.4x. Charted only.\n");
                continue;
            }

            foreach (var tier in BurnRateAlertPolicy.Tiers)
            {
                Alert(sb, slo, tier);
            }
        }

        sb.Append("  - name: slo-latency-quantiles\n");
        sb.Append("    rules:\n");
        foreach (var q in ChartedQuantiles)
        {
            var name = $"slo:latency_seconds:p{Number(q * 100)}_rate1m";
            var expr = $"histogram_quantile({Number(q)}, sum by (le) (rate({SloCatalog.DurationMetric}_bucket{{{SloCatalog.Selector},http_response_status_code!~\"5..\"}}[1m])))";
            sb.Append(CultureInfo.InvariantCulture, $"      - record: {name}\n        expr: {expr}\n");
        }

        return sb.ToString();
    }

    /// <summary>The error ratio of an SLO over a window: bad events / eligible events.</summary>
    public static string ErrorRatioExpression(SloDefinition slo, string window)
    {
        ArgumentNullException.ThrowIfNull(slo);
        var metric = SloCatalog.DurationMetric;
        var sel = SloCatalog.Selector;
        return slo.Kind switch
        {
            // "or vector(0)": before the first 5xx there is no 5xx series at all; the ratio is 0, not absent.
            SliKind.Availability =>
                $"(sum(rate({metric}_count{{{sel},http_response_status_code=~\"5..\"}}[{window}])) or vector(0))"
                + $" / sum(rate({metric}_count{{{sel}}}[{window}]))",
            // Latency is judged on requests that did not fail; failures are the availability SLO's budget.
            SliKind.Latency =>
                $"1 - sum(rate({metric}_bucket{{{sel},http_response_status_code!~\"5..\",le=\"{Number(slo.ThresholdSeconds!.Value)}\"}}[{window}]))"
                + $" / sum(rate({metric}_count{{{sel},http_response_status_code!~\"5..\"}}[{window}]))",
            _ => throw new ArgumentOutOfRangeException(nameof(slo), slo.Kind, "Unknown SLI kind."),
        };
    }

    public static string AlertName(SloDefinition slo, BurnRateTier tier) => slo.AlertPrefix + tier.Name;

    private static void Alert(StringBuilder sb, SloDefinition slo, BurnRateTier tier)
    {
        var longW = Duration(tier.LongWindow);
        var shortW = Duration(tier.ShortWindow);
        var threshold = Number(tier.BurnRate);
        var budgetPercent = Number(tier.BudgetConsumed * 100);
        var ci = CultureInfo.InvariantCulture;
        sb.Append(ci, $"      - alert: {AlertName(slo, tier)}\n");
        sb.Append(ci, $"        expr: {BurnRateRecord}{longW}{{slo=\"{slo.Name}\"}} > {threshold} and {BurnRateRecord}{shortW}{{slo=\"{slo.Name}\"}} > {threshold}\n");
        sb.Append("        labels:\n");
        sb.Append(ci, $"          severity: {tier.Severity}\n");
        sb.Append(ci, $"          slo: {slo.Name}\n");
        sb.Append(ci, $"          long_window: {longW}\n");
        sb.Append(ci, $"          short_window: {shortW}\n");
        sb.Append("        annotations:\n");
        sb.Append(ci, $"          summary: \"{slo.Name}: error budget burning faster than {threshold}x over {longW} and {shortW}\"\n");
        sb.Append(ci, $"          description: \"{slo.Description} Burn rate over {longW}: {{{{ $value | printf \\\"%.1f\\\" }}}}x. At {threshold}x, {budgetPercent}% of the 30-day budget is gone within {longW}.\"\n");
        sb.Append("          runbook_url: \"https://github.com/hekimcan206/slo-chaos-lab/blob/main/docs/slo.md#runbook\"\n");
    }

    private static void Record(StringBuilder sb, string name, string expr, string slo)
    {
        var ci = CultureInfo.InvariantCulture;
        sb.Append(ci, $"      - record: {name}\n");
        sb.Append(ci, $"        expr: {expr}\n");
        sb.Append("        labels:\n");
        sb.Append(ci, $"          slo: {slo}\n");
    }

    /// <summary>Prometheus duration syntax: 5m, 1h, 3d, 30d.</summary>
    public static string Duration(TimeSpan window) =>
        window.TotalDays >= 1 && window.TotalDays == Math.Floor(window.TotalDays) ? string.Create(CultureInfo.InvariantCulture, $"{window.TotalDays:0}d")
        : window.TotalHours >= 1 && window.TotalHours == Math.Floor(window.TotalHours) ? string.Create(CultureInfo.InvariantCulture, $"{window.TotalHours:0}h")
        : string.Create(CultureInfo.InvariantCulture, $"{window.TotalMinutes:0}m");

    /// <summary>Invariant, round-tripped through decimal so 1 - 0.999 prints as 0.001, not 0.0010000000000000009.</summary>
    public static string Number(double value) =>
        ((decimal)Math.Round(value, 10)).ToString("0.##########", CultureInfo.InvariantCulture);
}
