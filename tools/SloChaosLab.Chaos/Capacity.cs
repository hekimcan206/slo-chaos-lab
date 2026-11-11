namespace SloChaosLab.Chaos;

internal sealed record CapacityStep(int OfferedRatePerSecond, PhaseStats Stats);

/// <summary>
/// Baseline throughput: fixed-rate steps with no fault. Each step discards its first 10 s (connection pool and JIT
/// warm-up) and reports the rest. The sustainable throughput is the highest step whose p99 meets the latency SLO
/// with no errors.
/// </summary>
internal static class Capacity
{
    public static async Task<IReadOnlyList<CapacityStep>> RunAsync(
        RunSettings settings, IReadOnlyList<int> rates, TimeSpan step, TextWriter log, CancellationToken cancellationToken)
    {
        var generator = new LoadGenerator(settings.Api, settings.ClientTimeout);
        await Prewarm.RunAsync(generator, settings.Api, 500, log, cancellationToken);
        var steps = new List<CapacityStep>();
        foreach (var rate in rates)
        {
            log.WriteLine($"capacity step: {rate} req/s for {step.TotalSeconds:0} s");
            var samples = await generator.RunAsync(
                rate, step, Path.Combine(settings.OutputDirectory, "nbomber", $"capacity-{rate}"), null, cancellationToken);
            var stats = Analysis.Summarise(new Phase($"{rate} req/s", 10, step.TotalSeconds), samples);
            steps.Add(new CapacityStep(rate, stats));
            // Let queues drain between steps so one overloaded step does not bleed into the next.
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
        }

        return steps;
    }
}
