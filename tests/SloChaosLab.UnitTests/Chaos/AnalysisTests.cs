using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using SloChaosLab.Chaos;
using SloChaosLab.Slo;

namespace SloChaosLab.UnitTests.Chaos;

public sealed class AnalysisTests
{
    [Fact]
    public void Nearest_rank_percentile_returns_an_observed_value()
    {
        double[] sorted = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
        Analysis.Percentile(sorted, 50).Should().Be(5);
        Analysis.Percentile(sorted, 95).Should().Be(10);
        Analysis.Percentile(sorted, 99).Should().Be(10);
        Analysis.Percentile(sorted, 10).Should().Be(1);
        Analysis.Percentile([], 50).Should().Be(double.NaN);
    }

    public static Arbitrary<double[]> Latencies() =>
        Gen.ArrayOf(Gen.Choose(1, 100_000).Select(i => i / 10.0)).Where(a => a.Length > 0).Select(a => a.Order().ToArray()).ToArbitrary();

    [Property(Arbitrary = [typeof(AnalysisTests)], MaxTest = 300)]
    public bool Percentiles_are_monotonic_and_members_of_the_sample(double[] sorted)
    {
        var p50 = Analysis.Percentile(sorted, 50);
        var p95 = Analysis.Percentile(sorted, 95);
        var p99 = Analysis.Percentile(sorted, 99);
        return p50 <= p95 && p95 <= p99 && sorted.Contains(p50) && sorted.Contains(p99) && Analysis.Percentile(sorted, 100) == sorted[^1];
    }

    [Fact]
    public void Summary_splits_by_start_time_and_counts_every_failure_kind()
    {
        RequestSample[] samples =
        [
            new(0.5, 10, 201), new(1.5, 20, 201), new(1.6, 300, 201), new(1.7, 2000, RequestSample.ClientTimeout),
            new(1.8, 5, 503), new(1.9, 1, RequestSample.TransportError), new(2.5, 10, 201),
        ];

        var stats = Analysis.Summarise(new Phase("fault", 1, 2), samples);

        stats.Requests.Should().Be(5);
        stats.Succeeded.Should().Be(2);
        stats.ServerErrors.Should().Be(1);
        stats.ClientTimeouts.Should().Be(1);
        stats.TransportErrors.Should().Be(1);
        stats.Availability.Should().BeApproximately(2 / 5.0, 1e-12);
        stats.ThroughputPerSecond.Should().Be(2);
        stats.P99Ms.Should().Be(300);
        stats.P99AllMs.Should().Be(2000);
        stats.LatencyCompliance[SloCatalog.LatencyP99.Name].Should().Be(0.5);
    }

    [Fact]
    public void A_4xx_is_available_but_not_a_success()
    {
        var sample = new RequestSample(0, 3, 422);
        sample.IsAvailable.Should().BeTrue();
        sample.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void Budget_consumed_counts_unavailable_for_availability_and_slow_successes_for_latency()
    {
        RequestSample[] samples = [new(0, 10, 201), new(0, 300, 201), new(0, 5, 500), new(0, 2000, RequestSample.ClientTimeout)];
        var budget = Analysis.BudgetConsumed(samples, 50);
        var month = 50 * SloCatalog.Period.TotalSeconds;

        budget[SloCatalog.Availability.Name].Should().BeApproximately(2 / (0.001 * month), 1e-12);
        budget[SloCatalog.LatencyP99.Name].Should().BeApproximately(1 / (0.01 * month), 1e-12);
    }

    [Fact]
    public void Recovery_is_the_first_clean_ten_second_window_after_the_fault()
    {
        var samples = Enumerable.Range(0, 400)
            .Select(i => i / 10.0)
            .Select(t => new RequestSample(t, 10, t < 13 ? 503 : 201))
            .ToList();

        Analysis.RecoverySeconds(samples, faultEndSeconds: 10, runEndSeconds: 40).Should().Be(3);
        Analysis.RecoverySeconds([.. samples.Select(s => s with { Status = 503 })], 10, 40).Should().BeNull();
    }
}

public sealed class AlertModelTests
{
    private const double Rate = 50;

    private static List<RequestSample> Run(double seconds, Func<double, RequestSample> sample) =>
        [.. Enumerable.Range(0, (int)(seconds * Rate)).Select(i => sample(i / Rate))];

    [Fact]
    public void A_healthy_run_never_pages()
    {
        var samples = Run(300, t => new RequestSample(t, 8, 201));
        foreach (var tier in BurnRateAlertPolicy.Tiers)
        {
            AlertModel.FirstFiring(samples, SloCatalog.Availability, tier, Rate, 60, 300).Should().BeNull();
        }
    }

    [Fact]
    public void A_total_outage_pages_once_the_long_window_holds_one_point_four_four_percent_errors()
    {
        // Steady state: the hour before was healthy at the same rate. The 1 h ratio crosses 14.4 x 0.1 % = 1.44 %
        // after 0.0144 x 3600 = 51.8 s of 100 % errors; the 5 m window crossed it after 4.3 s.
        var samples = Run(300, t => new RequestSample(t, 5, t >= 60 ? 503 : 201));
        var fast = AlertModel.FirstFiring(samples, SloCatalog.Availability, BurnRateAlertPolicy.Tiers[0], Rate, 60, 300);
        fast.Should().BeInRange(51, 54);
    }

    [Fact]
    public void Slow_successes_burn_the_latency_budget_but_not_the_availability_budget()
    {
        var samples = Run(120, t => new RequestSample(t, 400, 201));
        AlertModel.Burn(samples, SloCatalog.LatencyP99, Rate, 120, 60).Should().BeApproximately(100, 1e-9);
        AlertModel.Burn(samples, SloCatalog.Availability, Rate, 120, 60).Should().Be(0);
    }
}
