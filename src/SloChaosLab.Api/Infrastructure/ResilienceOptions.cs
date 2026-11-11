namespace SloChaosLab.Api.Infrastructure;

/// <summary>Request-path resilience around the database (docs/adr/0004). Disabled in the baseline profile.</summary>
internal sealed class ResilienceOptions
{
    public const string Section = "Resilience";

    public bool Enabled { get; set; }

    /// <summary>
    /// Total time one request may spend on the database, retries included. Deliberately shorter than the 2 s
    /// callers wait: the service must answer (503) before the caller gives up, otherwise the failure never reaches
    /// the server-side SLI (experiment 5).
    /// </summary>
    public int RequestDeadlineMs { get; set; } = 1500;

    public int AttemptTimeoutMs { get; set; } = 1000;

    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>Base of the exponential backoff; Polly adds decorrelated jitter so retries do not synchronise.</summary>
    public int RetryBaseDelayMs { get; set; } = 50;

    public double BreakerFailureRatio { get; set; } = 0.5;

    public int BreakerMinimumThroughput { get; set; } = 20;

    public int BreakerSamplingSeconds { get; set; } = 10;

    public int BreakerBreakSeconds { get; set; } = 5;

    /// <summary>Posting requests allowed in flight; the rest are shed with 503 instead of queueing (experiment 3).</summary>
    public int MaxConcurrentPostings { get; set; } = 64;
}

/// <summary>Client-side database settings applied to the request-path connection string, when set.</summary>
internal sealed class DatabaseOptions
{
    public const string Section = "Database";

    public bool MigrateOnStartup { get; set; }

    public int? ConnectTimeoutSeconds { get; set; }

    public int? CommandTimeoutSeconds { get; set; }

    public int? CancellationTimeoutMs { get; set; }

    public int? MaxPoolSize { get; set; }
}
