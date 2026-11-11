namespace SloChaosLab.Api.Infrastructure;

/// <summary>
/// Named bundles of settings, so a before/after experiment pair differs by one environment variable
/// (<c>Lab__Profile</c>) and nothing else. Any individual key set explicitly (environment, appsettings) still wins:
/// the profile is inserted as the lowest-priority configuration source.
/// </summary>
internal static class LabProfile
{
    public const string Baseline = "baseline";
    public const string Hardened = "hardened";

    /// <summary>
    /// <c>baseline</c>: the service as ledger-recon shipped it. Interactive posting (4 round trips), Npgsql defaults
    /// (15 s connect and pool-wait timeout, 30 s command timeout, 100 connections), no deadline, retry, breaker or
    /// load shedding. <c>hardened</c>: every fix the experiments motivated (see docs/postmortems).
    /// </summary>
    public static IReadOnlyDictionary<string, string?> Defaults(string? profile) => profile?.ToLowerInvariant() switch
    {
        null or "" or Baseline => new Dictionary<string, string?>
        {
            ["Ledger:PostingMode"] = "Interactive",
            ["Resilience:Enabled"] = "false",
        },
        Hardened => new Dictionary<string, string?>
        {
            // Experiment 1/2: one round trip per posting.
            ["Ledger:PostingMode"] = "Function",
            // Experiments 3-5: deadline, jittered retries, breaker, shedding, tight client-side timeouts.
            ["Resilience:Enabled"] = "true",
            ["Resilience:RequestDeadlineMs"] = "1500",
            ["Resilience:AttemptTimeoutMs"] = "1000",
            ["Resilience:MaxRetryAttempts"] = "3",
            ["Resilience:RetryBaseDelayMs"] = "50",
            ["Resilience:BreakerFailureRatio"] = "0.5",
            ["Resilience:BreakerMinimumThroughput"] = "20",
            ["Resilience:BreakerSamplingSeconds"] = "10",
            ["Resilience:BreakerBreakSeconds"] = "5",
            ["Resilience:MaxConcurrentPostings"] = "64",
            ["Database:ConnectTimeoutSeconds"] = "2",
            ["Database:CommandTimeoutSeconds"] = "2",
            ["Database:CancellationTimeoutMs"] = "-1",
            ["Database:MaxPoolSize"] = "32",
        },
        _ => throw new InvalidOperationException($"Unknown Lab:Profile '{profile}'. Use '{Baseline}' or '{Hardened}'."),
    };
}
