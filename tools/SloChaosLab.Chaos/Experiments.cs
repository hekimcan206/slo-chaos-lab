namespace SloChaosLab.Chaos;

/// <summary>One change to the network link at a point in time, relative to the start of the fault phase.</summary>
internal sealed record FaultAction(TimeSpan At, Toxic? Add = null, string? Remove = null);

/// <summary>
/// A fault injected on the link between the ledger service and PostgreSQL. Every experiment runs the same shape:
/// warm-up at the fixed rate, the fault for a fixed duration, then recovery at the same rate, so the before/after
/// runs of an experiment differ only in the service build/configuration under test.
/// </summary>
internal sealed record Experiment(string Name, string Title, string Fault, Func<TimeSpan, IReadOnlyList<FaultAction>> Schedule)
{
    /// <summary>A toxic that is added at the start of the fault phase and removed at its end.</summary>
    public static Func<TimeSpan, IReadOnlyList<FaultAction>> Constant(params Toxic[] toxics) => duration =>
        [.. toxics.Select(t => new FaultAction(TimeSpan.Zero, Add: t)), .. toxics.Select(t => new FaultAction(duration, Remove: t.Name))];
}

internal static class ExperimentCatalog
{
    private static Dictionary<string, int> Attrs(params (string Key, int Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value);

    /// <summary>+100 ms on every response from PostgreSQL: a cross-zone hop, a congested link, a slow proxy.</summary>
    public static Experiment Latency { get; } = new(
        "latency",
        "Latency: +100 ms on every database response",
        "Toxiproxy `latency` toxic, downstream, latency=100 ms, jitter=0",
        Experiment.Constant(new Toxic("db-latency", "latency", "downstream", 1.0, Attrs(("latency", 100), ("jitter", 0)))));

    /// <summary>40 ms +/- 40 ms per response: the same mean shift as a mildly bad link, but with a wide spread.</summary>
    public static Experiment Jitter { get; } = new(
        "jitter",
        "Jitter: 0-80 ms random delay on every database response",
        "Toxiproxy `latency` toxic, downstream, latency=40 ms, jitter=40 ms (uniform 0-80 ms)",
        Experiment.Constant(new Toxic("db-jitter", "latency", "downstream", 1.0, Attrs(("latency", 40), ("jitter", 40)))));

    /// <summary>Per-connection bandwidth cap on the query direction: a saturated link or a throttled tunnel.</summary>
    public static Experiment Bandwidth { get; } = new(
        "bandwidth",
        "Bandwidth: queries capped at 4 KB/s per connection",
        "Toxiproxy `bandwidth` toxic, upstream, rate=4 KB/s per connection",
        Experiment.Constant(new Toxic("db-bandwidth", "bandwidth", "upstream", 1.0, Attrs(("rate", 4)))));

    /// <summary>
    /// TCP resets in short bursts: every connection that sends data during a 500 ms pulse is reset, once every 10 s.
    /// This is what a proxy or load-balancer restart, a NAT table flush or a database failover looks like from the
    /// client: in-flight statements fail and pooled connections die, but the database itself is fine.
    /// </summary>
    public static Experiment Reset { get; } = new(
        "reset",
        "Connection reset: RST bursts (500 ms every 10 s)",
        "Toxiproxy `reset_peer` toxic, upstream, timeout=0, toggled on for 500 ms every 10 s",
        duration =>
        {
            var actions = new List<FaultAction>();
            for (var t = TimeSpan.Zero; t < duration; t += TimeSpan.FromSeconds(10))
            {
                actions.Add(new FaultAction(t, Add: new Toxic("db-reset", "reset_peer", "upstream", 1.0, Attrs(("timeout", 0)))));
                actions.Add(new FaultAction(t + TimeSpan.FromMilliseconds(500), Remove: "db-reset"));
            }

            return actions;
        });

    /// <summary>
    /// A full partition as the network really does it: packets are silently dropped in both directions, nothing is
    /// refused or reset, and connections stay open. Clients find out only through their own timeouts.
    /// </summary>
    public static Experiment Partition { get; } = new(
        "partition",
        "Partition: service and database cannot exchange a single byte",
        "Toxiproxy `timeout` toxic, timeout=0 (black hole, connections never closed), upstream and downstream",
        Experiment.Constant(
            new Toxic("db-blackhole-up", "timeout", "upstream", 1.0, Attrs(("timeout", 0))),
            new Toxic("db-blackhole-down", "timeout", "downstream", 1.0, Attrs(("timeout", 0)))));

    public static Experiment None { get; } = new("baseline", "Baseline: no fault", "none", _ => []);

    public static IReadOnlyList<Experiment> All { get; } = [None, Latency, Jitter, Bandwidth, Reset, Partition];

    public static Experiment Find(string name) =>
        All.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"Unknown experiment '{name}'. Known: {string.Join(", ", All.Select(e => e.Name))}.", nameof(name));
}
