using SloChaosLab.Chaos;

namespace SloChaosLab.UnitTests.Chaos;

public sealed class ExperimentCatalogTests
{
    private static readonly TimeSpan Fault = TimeSpan.FromSeconds(120);

    [Fact]
    public void Every_fault_covers_the_brief_and_names_are_unique()
    {
        ExperimentCatalog.All.Select(e => e.Name).Should().OnlyHaveUniqueItems();
        ExperimentCatalog.All.SelectMany(e => e.Schedule(Fault)).Where(a => a.Add is not null).Select(a => a.Add!.Type).Distinct()
            .Should().BeEquivalentTo("latency", "bandwidth", "reset_peer", "timeout");
    }

    [Theory]
    [InlineData("latency")]
    [InlineData("jitter")]
    [InlineData("bandwidth")]
    [InlineData("reset")]
    [InlineData("partition")]
    public void Every_toxic_added_is_removed_by_the_end_of_the_fault(string name)
    {
        var schedule = ExperimentCatalog.Find(name).Schedule(Fault);
        var active = new HashSet<string>(StringComparer.Ordinal);
        foreach (var action in schedule.OrderBy(a => a.At))
        {
            action.At.Should().BeLessThanOrEqualTo(Fault);
            if (action.Add is not null)
            {
                active.Add(action.Add.Name).Should().BeTrue("a toxic is never added twice without being removed");
            }
            else
            {
                active.Remove(action.Remove!).Should().BeTrue("only an active toxic is removed");
            }
        }

        active.Should().BeEmpty();
    }

    [Fact]
    public void Reset_pulses_last_half_a_second_every_ten_seconds()
    {
        var adds = ExperimentCatalog.Reset.Schedule(Fault).Where(a => a.Add is not null).Select(a => a.At.TotalSeconds).ToList();
        adds.Should().HaveCount(12).And.BeInAscendingOrder();
        adds.Zip(adds.Skip(1), (a, b) => b - a).Should().AllSatisfy(gap => gap.Should().Be(10));
        ExperimentCatalog.Reset.Schedule(Fault).Where(a => a.Remove is not null).First().At.Should().Be(TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public void Partition_black_holes_both_directions()
    {
        var toxics = ExperimentCatalog.Partition.Schedule(Fault).Where(a => a.Add is not null).Select(a => a.Add!).ToList();
        toxics.Select(t => t.Stream).Should().BeEquivalentTo("upstream", "downstream");
        toxics.Should().AllSatisfy(t => t.Attributes["timeout"].Should().Be(0));
    }

    [Fact]
    public void Unknown_experiments_are_rejected_with_the_known_names() =>
        FluentActions.Invoking(() => ExperimentCatalog.Find("meteor")).Should().Throw<ArgumentException>().WithMessage("*partition*");

    [Theory]
    [InlineData("1.5", 1.5)]
    [InlineData("NaN", double.NaN)]
    [InlineData("+Inf", double.PositiveInfinity)]
    [InlineData("-Inf", double.NegativeInfinity)]
    public void Prometheus_sample_values_parse_including_special_values(string raw, double expected) =>
        PrometheusClient.ParseSample(raw).Should().Be(expected);
}
