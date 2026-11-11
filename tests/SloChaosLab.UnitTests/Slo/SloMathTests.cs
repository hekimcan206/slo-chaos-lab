using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using SloChaosLab.Slo;

namespace SloChaosLab.UnitTests.Slo;

public sealed class SloMathTests
{
    private static readonly TimeSpan Month = TimeSpan.FromDays(30);

    [Theory]
    [InlineData(0.001, 0.999, 1.0)]
    [InlineData(0.0144, 0.999, 14.4)]
    [InlineData(0.2, 0.99, 20.0)]
    [InlineData(0.0, 0.999, 0.0)]
    public void Burn_rate_is_error_ratio_over_budget(double errorRatio, double objective, double expected) =>
        SloMath.BurnRate(errorRatio, objective).Should().BeApproximately(expected, 1e-9);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(double.NaN)]
    public void Objective_must_be_strictly_between_zero_and_one(double objective) =>
        FluentActions.Invoking(() => SloMath.BurnRate(0.1, objective)).Should().Throw<ArgumentOutOfRangeException>();

    [Fact]
    public void Two_minutes_of_total_outage_at_fixed_rate_uses_the_expected_share_of_a_99_9_budget()
    {
        // 50 req/s for 30 days at 99.9 % allows 129,600 failures; a 120 s outage fails 6,000 of them.
        SloMath.BudgetConsumed(6_000, 50, 0.999, Month).Should().BeApproximately(6_000 / 129_600.0, 1e-12);
    }

    [Fact]
    public void Compliance_without_traffic_is_perfect() => SloMath.Compliance(0, 0).Should().Be(1);

    [Fact]
    public void Time_to_exhaustion_is_infinite_when_nothing_burns() => SloMath.TimeToExhaustion(0, Month).Should().Be(TimeSpan.MaxValue);

    public static Arbitrary<double> Objectives() =>
        Gen.Elements(0.9, 0.95, 0.99, 0.995, 0.999, 0.9995, 0.9999).ToArbitrary();

    [Property(Arbitrary = [typeof(SloMathTests)])]
    public bool Burning_at_rate_one_for_the_whole_period_spends_exactly_the_budget(double objective)
    {
        // Error ratio == budget for every event of the period -> 100 % of the budget, independent of the rate.
        const double rate = 37.5;
        var events = rate * Month.TotalSeconds;
        var bad = (long)Math.Round((1 - objective) * events);
        return Math.Abs(SloMath.BudgetConsumed(bad, rate, objective, Month) - 1) < 1e-6
               && Math.Abs(SloMath.BudgetConsumedByBurn(1, Month, Month) - 1) < 1e-12;
    }

    [Property(MaxTest = 200)]
    public bool Time_to_exhaustion_times_burn_rate_is_the_period(PositiveInt burnTimesTen)
    {
        var burn = burnTimesTen.Get / 10.0;
        var tte = SloMath.TimeToExhaustion(burn, Month);
        return Math.Abs((tte.TotalSeconds * burn) - Month.TotalSeconds) < 1;
    }

    [Property(MaxTest = 200)]
    public bool Budget_consumed_is_monotonic_in_bad_events(NonNegativeInt a, NonNegativeInt b)
    {
        var (low, high) = a.Get <= b.Get ? (a.Get, b.Get) : (b.Get, a.Get);
        return SloMath.BudgetConsumed(low, 50, 0.999, Month) <= SloMath.BudgetConsumed(high, 50, 0.999, Month);
    }
}
