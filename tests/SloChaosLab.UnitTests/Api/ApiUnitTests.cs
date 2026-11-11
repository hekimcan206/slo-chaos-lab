using SloChaosLab.Api.Features;
using SloChaosLab.Api.Infrastructure;
using SloChaosLab.Api.Ledger;

namespace SloChaosLab.UnitTests.Api;

public sealed class ApiUnitTests
{
    [Theory]
    [InlineData("abc", "abc")]
    [InlineData("\"abc\"", "abc")]
    [InlineData("  k-1  ", "k-1")]
    [InlineData("", null)]
    [InlineData("has space", null)]
    [InlineData(null, null)]
    public void Idempotency_keys_accept_bare_and_structured_field_forms(string? raw, string? expected) =>
        LedgerEndpoints.NormaliseKey(raw).Should().Be(expected);

    [Fact]
    public void Hardened_deadline_is_shorter_than_the_callers_two_second_timeout()
    {
        // Experiment 5: if the service answers after its caller gave up, the failure is a 499 the SLI never sees.
        var hardened = LabProfile.Defaults(LabProfile.Hardened);
        int.Parse(hardened["Resilience:RequestDeadlineMs"]!, System.Globalization.CultureInfo.InvariantCulture).Should().BeLessThan(2000);
        int.Parse(hardened["Resilience:AttemptTimeoutMs"]!, System.Globalization.CultureInfo.InvariantCulture)
            .Should().BeLessThan(int.Parse(hardened["Resilience:RequestDeadlineMs"]!, System.Globalization.CultureInfo.InvariantCulture));
        hardened["Ledger:PostingMode"].Should().Be(nameof(PostingMode.Function));
    }

    [Fact]
    public void Baseline_keeps_the_ledger_recon_behaviour()
    {
        var baseline = LabProfile.Defaults(null);
        baseline["Ledger:PostingMode"].Should().Be(nameof(PostingMode.Interactive));
        baseline["Resilience:Enabled"].Should().Be("false");
        baseline.Should().NotContainKey("Database:CommandTimeoutSeconds", "the baseline runs on Npgsql defaults");
    }

    [Fact]
    public void Unknown_profiles_fail_fast() =>
        FluentActions.Invoking(() => LabProfile.Defaults("yolo")).Should().Throw<InvalidOperationException>();

    [Theory]
    [InlineData(typeof(Npgsql.NpgsqlException), true)]
    [InlineData(typeof(TimeoutException), true)]
    [InlineData(typeof(Polly.Timeout.TimeoutRejectedException), true)]
    [InlineData(typeof(InvalidOperationException), false)]
    public void Only_database_and_timeout_failures_count_as_dependency_failures(Type type, bool expected) =>
        PostingService.IsDependencyFailure((Exception)Activator.CreateInstance(type)!).Should().Be(expected);
}
