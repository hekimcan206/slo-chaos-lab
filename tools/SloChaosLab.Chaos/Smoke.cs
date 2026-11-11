using System.Collections.Concurrent;
using System.Diagnostics;

namespace SloChaosLab.Chaos;

/// <summary>
/// The CI chaos check: steady load, then a full partition, and the page-level burn-rate alert must fire before the
/// deadline. It also asserts the opposite: nothing fires during the healthy warm-up (no false page).
/// </summary>
internal static class Smoke
{
    public static async Task<bool> RunAsync(RunSettings settings, string alert, TimeSpan deadline, TextWriter log, CancellationToken cancellationToken)
    {
        using var toxiHttp = new HttpClient { BaseAddress = settings.Toxiproxy, Timeout = TimeSpan.FromSeconds(10) };
        using var promHttp = new HttpClient { BaseAddress = settings.Prometheus, Timeout = TimeSpan.FromSeconds(10) };
        using var handler = new SocketsHttpHandler { MaxConnectionsPerServer = 1024 };
        using var apiHttp = new HttpClient(handler) { BaseAddress = settings.Api, Timeout = Timeout.InfiniteTimeSpan };
        var toxiproxy = new ToxiproxyClient(toxiHttp, "postgres");
        var prometheus = new PrometheusClient(promHttp);
        var generator = new LoadGenerator(settings.Api, settings.ClientTimeout);

        await ApiReadiness.WaitAsync(settings.Api, TimeSpan.FromMinutes(3), log, cancellationToken);
        await toxiproxy.EnsureProxyAsync(cancellationToken);
        await toxiproxy.ResetAsync(cancellationToken);

        using var loadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var samples = new ConcurrentQueue<RequestSample>();
        var clock = Stopwatch.StartNew();
        var load = Task.Run(async () =>
        {
            // Open-model load without NBomber: the smoke test needs to stop as soon as the alert fires.
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            var inflight = new List<Task>();
            while (await timer.WaitForNextTickAsync(loadCts.Token))
            {
                for (var i = 0; i < settings.RatePerSecond; i++)
                {
                    inflight.Add(Task.Run(async () => samples.Enqueue(await generator.PostOnceAsync(apiHttp, clock.Elapsed.TotalSeconds, loadCts.Token)), loadCts.Token));
                }

                inflight.RemoveAll(t => t.IsCompleted);
            }
        }, loadCts.Token);

        try
        {
            await Task.Delay(settings.Warmup, cancellationToken);
            var preFault = await prometheus.FiringAlertsAsync(cancellationToken);
            var okBefore = samples.Count(s => s.IsSuccess);
            log.WriteLine($"warm-up done: {samples.Count} requests, {okBefore} succeeded; firing alerts: [{string.Join(", ", preFault)}]");
            if (okBefore == 0 || preFault.Count > 0)
            {
                log.WriteLine("FAIL: the healthy warm-up must succeed and must not fire any alert.");
                return false;
            }

            foreach (var action in settings.Experiment.Schedule(deadline).Where(a => a.Add is not null))
            {
                await toxiproxy.AddAsync(action.Add!, cancellationToken);
            }

            var faultAt = clock.Elapsed;
            log.WriteLine($"t={faultAt.TotalSeconds:0}s partition injected; waiting up to {deadline.TotalSeconds:0}s for {alert}");
            while (clock.Elapsed - faultAt < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                var firing = await prometheus.FiringAlertsAsync(cancellationToken);
                if (firing.Contains(alert))
                {
                    log.WriteLine($"PASS: {alert} fired {(clock.Elapsed - faultAt).TotalSeconds:0}s after the partition (firing: {string.Join(", ", firing)})");
                    return true;
                }
            }

            log.WriteLine($"FAIL: {alert} did not fire within {deadline.TotalSeconds:0}s of the partition.");
            return false;
        }
        finally
        {
            await loadCts.CancelAsync();
            try
            {
                await load;
            }
            catch (OperationCanceledException)
            {
                // expected: the load loop stops with the test
            }

            await toxiproxy.ResetAsync(CancellationToken.None);
        }
    }
}
