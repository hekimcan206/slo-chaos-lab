using SloChaosLab.Slo;

namespace SloChaosLab.UnitTests.Slo;

public sealed class BurnRateAlertPolicyTests
{
    [Fact]
    public void Thresholds_follow_from_budget_consumed_period_and_long_window_as_in_the_SRE_workbook()
    {
        // burn rate = budget consumed x period / long window: 2 % in 1 h = 14.4, 5 % in 6 h = 6, 10 % in 3 d = 1.
        foreach (var tier in BurnRateAlertPolicy.Tiers)
        {
            var derived = tier.BudgetConsumed * SloCatalog.Period.TotalHours / tier.LongWindow.TotalHours;
            derived.Should().BeApproximately(tier.BurnRate, 1e-9, tier.Name);
        }

        BurnRateAlertPolicy.Tiers.Select(t => t.BurnRate).Should().Equal(14.4, 6, 1);
    }

    [Fact]
    public void Short_windows_are_one_twelfth_of_long_windows() =>
        BurnRateAlertPolicy.Tiers.Should().AllSatisfy(t => (t.LongWindow / t.ShortWindow).Should().BeApproximately(12, 1e-9));

    [Fact]
    public void Recording_windows_are_distinct_and_sorted() =>
        BurnRateAlertPolicy.Windows.Select(PrometheusRuleGenerator.Duration).Should().Equal("5m", "30m", "1h", "6h", "3d");
}

public sealed class SloCatalogTests
{
    [Fact]
    public void Latency_thresholds_are_exact_bucket_boundaries_of_the_aspnetcore_histogram()
    {
        // Default http.server.request.duration buckets in ASP.NET Core 8+.
        double[] buckets = [0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10];
        SloCatalog.All.Where(s => s.Kind == SliKind.Latency).Should().AllSatisfy(s => buckets.Should().Contain(s.ThresholdSeconds!.Value));
    }

    [Fact]
    public void A_p50_objective_cannot_be_alerted_on_with_multi_burn_rate_and_the_others_can()
    {
        SloCatalog.LatencyP50.IsAlertable.Should().BeFalse("14.4 x a 50 % budget is more than every request failing");
        SloCatalog.Availability.IsAlertable.Should().BeTrue();
        SloCatalog.LatencyP95.IsAlertable.Should().BeTrue();
        SloCatalog.LatencyP99.IsAlertable.Should().BeTrue();
    }

    [Fact]
    public void Names_are_unique_and_alert_prefixes_are_pascal_case()
    {
        SloCatalog.All.Select(s => s.Name).Should().OnlyHaveUniqueItems();
        SloCatalog.LatencyP99.AlertPrefix.Should().Be("LedgerPostLatencyP99");
        PrometheusRuleGenerator.AlertName(SloCatalog.Availability, BurnRateAlertPolicy.Tiers[0]).Should().Be("LedgerPostAvailabilityFastBurn");
    }
}

public sealed class PrometheusRuleGeneratorTests
{
    [Fact]
    public void Committed_rules_equal_the_generated_rules()
    {
        // Golden test: change SloCatalog, then run `chaos generate-rules`; this fails until both agree.
        var committed = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "golden", "slo-rules.yml")).Replace("\r\n", "\n", StringComparison.Ordinal);
        PrometheusRuleGenerator.Generate(SloCatalog.All).Should().Be(committed);
    }

    [Fact]
    public void Every_alertable_slo_gets_one_alert_per_tier_and_the_p50_gets_none()
    {
        var yaml = PrometheusRuleGenerator.Generate(SloCatalog.All);
        foreach (var slo in SloCatalog.All)
        {
            foreach (var tier in BurnRateAlertPolicy.Tiers)
            {
                yaml.Contains($"alert: {PrometheusRuleGenerator.AlertName(slo, tier)}\n", StringComparison.Ordinal).Should().Be(slo.IsAlertable);
            }
        }
    }

    [Fact]
    public void Alerts_have_no_for_clause_and_require_both_windows()
    {
        var yaml = PrometheusRuleGenerator.Generate(SloCatalog.All);
        yaml.Should().NotContain("\n        for:");
        yaml.Should().Contain("expr: slo:burn_rate:rate1h{slo=\"ledger-post-availability\"} > 14.4 and slo:burn_rate:rate5m{slo=\"ledger-post-availability\"} > 14.4");
    }

    [Fact]
    public void Availability_ratio_counts_5xx_and_is_zero_rather_than_absent_before_the_first_error()
    {
        var expr = PrometheusRuleGenerator.ErrorRatioExpression(SloCatalog.Availability, "5m");
        expr.Should().Contain("http_response_status_code=~\"5..\"").And.Contain("or vector(0)");
    }

    [Fact]
    public void Latency_ratio_reads_the_threshold_bucket_and_excludes_failed_requests()
    {
        var expr = PrometheusRuleGenerator.ErrorRatioExpression(SloCatalog.LatencyP99, "1h");
        expr.Should().Contain("le=\"0.25\"").And.Contain("http_response_status_code!~\"5..\"").And.Contain("[1h]");
    }

    [Theory]
    [InlineData(300, "5m")]
    [InlineData(3600, "1h")]
    [InlineData(5400, "90m")]
    [InlineData(259200, "3d")]
    public void Durations_use_prometheus_syntax(int seconds, string expected) =>
        PrometheusRuleGenerator.Duration(TimeSpan.FromSeconds(seconds)).Should().Be(expected);

    [Theory]
    [InlineData(1 - 0.999, "0.001")]
    [InlineData(14.4, "14.4")]
    [InlineData(0.25, "0.25")]
    [InlineData(6, "6")]
    public void Numbers_are_invariant_and_free_of_floating_point_noise(double value, string expected) =>
        PrometheusRuleGenerator.Number(value).Should().Be(expected);
}
