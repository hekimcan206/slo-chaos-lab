namespace SloChaosLab.Slo;

/// <summary>
/// The SLOs of the posting endpoint, the only user-facing write path of the ledger service. The thresholds were set
/// from the measured baseline (docs/benchmarks.md) with headroom, not picked first: baseline p99 is well under 250 ms on
/// this hardware, so a breach means something changed, not that the target was aspirational. See docs/slo.md.
/// </summary>
public static class SloCatalog
{
    public static readonly TimeSpan Period = TimeSpan.FromDays(30);

    public static SloDefinition Availability { get; } = new(
        "ledger-post-availability",
        "99.9% of journal-entry posts do not fail with a 5xx.",
        SliKind.Availability,
        0.999,
        Period);

    public static SloDefinition LatencyP50 { get; } = new(
        "ledger-post-latency-p50",
        "50% of successful journal-entry posts complete within 25 ms.",
        SliKind.Latency,
        0.50,
        Period,
        0.025);

    public static SloDefinition LatencyP95 { get; } = new(
        "ledger-post-latency-p95",
        "95% of successful journal-entry posts complete within 100 ms.",
        SliKind.Latency,
        0.95,
        Period,
        0.1);

    public static SloDefinition LatencyP99 { get; } = new(
        "ledger-post-latency-p99",
        "99% of successful journal-entry posts complete within 250 ms.",
        SliKind.Latency,
        0.99,
        Period,
        0.25);

    public static IReadOnlyList<SloDefinition> All { get; } = [Availability, LatencyP50, LatencyP95, LatencyP99];

    /// <summary>Label selector for the SLO-bearing requests in the OpenTelemetry HTTP server metric.</summary>
    public const string Selector =
        "job=\"ledger-api\",http_route=\"/api/v1/journal-entries\",http_request_method=\"POST\"";

    public const string DurationMetric = "http_server_request_duration_seconds";
}
