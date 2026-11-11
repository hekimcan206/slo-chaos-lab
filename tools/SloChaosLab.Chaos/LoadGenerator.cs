using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using NBomber.Contracts.Stats;
using NBomber.CSharp;

namespace SloChaosLab.Chaos;

/// <summary>What one request looked like from the caller's side.</summary>
/// <param name="StartSeconds">Seconds since the run started, when the request was sent.</param>
/// <param name="Status">HTTP status, or <see cref="ClientTimeout"/> / <see cref="TransportError"/>.</param>
internal sealed record RequestSample(double StartSeconds, double LatencyMs, int Status)
{
    public const int ClientTimeout = 0;
    public const int TransportError = -1;

    /// <summary>Good for the availability SLO: anything but a 5xx, a timeout or a transport error.</summary>
    public bool IsAvailable => Status is >= 200 and < 500;

    public bool IsSuccess => Status is >= 200 and < 300;
}

/// <summary>
/// Open-model load: NBomber injects a fixed number of new requests every second regardless of how many are still
/// in flight, the way independent users behave. A closed model (N looping workers) would slow down with the service
/// and hide the very queueing the experiments are about (coordinated omission).
/// </summary>
internal sealed class LoadGenerator(Uri apiBase, TimeSpan clientTimeout, int accounts = 1000)
{
    public async Task<IReadOnlyList<RequestSample>> RunAsync(
        int ratePerSecond, TimeSpan duration, string reportFolder, Func<Stopwatch, CancellationToken, Task>? sideTask, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ratePerSecond);
        var samples = new ConcurrentQueue<RequestSample>();
        using var handler = new SocketsHttpHandler
        {
            // Enough connections for every in-flight request: the client must never be the bottleneck it measures.
            MaxConnectionsPerServer = 2048,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        using var http = new HttpClient(handler) { BaseAddress = apiBase, Timeout = Timeout.InfiniteTimeSpan };
        var clock = Stopwatch.StartNew();

        var scenario = Scenario.Create("post_journal_entry", async context =>
            {
                var started = clock.Elapsed.TotalSeconds;
                var sample = await PostOnceAsync(http, started, context.ScenarioCancellationToken);
                samples.Enqueue(sample);
                var code = sample.Status.ToString(CultureInfo.InvariantCulture);
                return sample.IsSuccess
                    ? Response.Ok(statusCode: code)
                    : Response.Fail(statusCode: code);
            })
            .WithoutWarmUp()
            .WithLoadSimulations(Simulation.Inject(ratePerSecond, TimeSpan.FromSeconds(1), duration));

        using var sideCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var side = sideTask is null ? Task.CompletedTask : Task.Run(() => sideTask(clock, sideCts.Token), sideCts.Token);

        NBomberRunner
            .RegisterScenarios(scenario)
            .WithTestSuite("slo-chaos-lab")
            .WithReportFolder(reportFolder)
            .WithReportFormats(ReportFormat.Md)
            .Run();

        await sideCts.CancelAsync();
        try
        {
            await side;
        }
        catch (OperationCanceledException)
        {
            // The fault schedule outlived the load phase; it is cancelled and the caller resets Toxiproxy.
        }

        return [.. samples.OrderBy(s => s.StartSeconds)];
    }

    internal async Task<RequestSample> PostOnceAsync(HttpClient http, double started, CancellationToken cancellationToken)
    {
        var from = Random.Shared.Next(1, accounts + 1);
        var to = Random.Shared.Next(1, accounts);
        if (to >= from)
        {
            to++;
        }

        var amount = Random.Shared.Next(100, 50_000) / 100m;
        var body = new
        {
            description = "Synthetic transfer",
            currency = "TRY",
            lines = new[]
            {
                new { accountCode = $"CUST-{from:D5}", direction = "debit", amount },
                new { accountCode = $"CUST-{to:D5}", direction = "credit", amount },
            },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/journal-entries") { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(clientTimeout);
        var sw = Stopwatch.StartNew();
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            return new RequestSample(started, sw.Elapsed.TotalMilliseconds, (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The caller gave up; the server may still finish the work later. This is the client-observed failure
            // a server-side SLI can miss, which is why both are reported.
            return new RequestSample(started, sw.Elapsed.TotalMilliseconds, RequestSample.ClientTimeout);
        }
        catch (HttpRequestException)
        {
            return new RequestSample(started, sw.Elapsed.TotalMilliseconds, RequestSample.TransportError);
        }
    }
}
