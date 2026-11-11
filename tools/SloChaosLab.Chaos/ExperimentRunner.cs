using System.Diagnostics;
using System.Globalization;
using SloChaosLab.Slo;

namespace SloChaosLab.Chaos;

internal sealed record RunSettings(
    Experiment Experiment,
    string Variant,
    int RatePerSecond,
    TimeSpan Warmup,
    TimeSpan Fault,
    TimeSpan Recovery,
    TimeSpan ClientTimeout,
    Uri Api,
    Uri Toxiproxy,
    Uri Prometheus,
    string OutputDirectory);

/// <summary>Server-side numbers for one phase, read back from Prometheus (the SLI as the alerts see it).</summary>
internal sealed record ServerPhaseStats(
    string Phase, double Requests, double ServerErrors, double ClientClosed, double Availability, IReadOnlyDictionary<string, double> LatencyCompliance);

internal sealed record AlertTiming(string Alert, double? FirstFiringAfterFaultStartSeconds, double? LastFiringAfterFaultEndSeconds);

internal sealed record ModelledAlert(string Alert, double? FirstFiringAfterFaultStartSeconds);

internal sealed record RunResult(
    string Experiment,
    string Title,
    string Fault,
    string Variant,
    DateTimeOffset StartedAt,
    int RatePerSecond,
    double WarmupSeconds,
    double FaultSeconds,
    double RecoverySeconds,
    double ClientTimeoutSeconds,
    IReadOnlyList<PhaseStats> Client,
    IReadOnlyList<ServerPhaseStats> Server,
    IReadOnlyDictionary<string, double> BudgetConsumedClient,
    IReadOnlyDictionary<string, double> BudgetConsumedServer,
    IReadOnlyDictionary<string, double> MaxBurnRate5m,
    IReadOnlyDictionary<string, double> MaxBurnRate1h,
    IReadOnlyList<AlertTiming> Alerts,
    IReadOnlyList<ModelledAlert> ModelledSteadyStateAlerts,
    double? RecoveryAfterFaultSeconds,
    string Host);

/// <summary>Runs one experiment end to end: clean link, load + fault schedule, then read the SLO back from Prometheus.</summary>
internal sealed class ExperimentRunner(RunSettings settings, TextWriter log)
{
    private static readonly TimeSpan PrometheusStep = TimeSpan.FromSeconds(5);

    public async Task<RunResult> RunAsync(CancellationToken cancellationToken)
    {
        using var toxiHttp = new HttpClient { BaseAddress = settings.Toxiproxy, Timeout = TimeSpan.FromSeconds(10) };
        using var promHttp = new HttpClient { BaseAddress = settings.Prometheus, Timeout = TimeSpan.FromSeconds(30) };
        var toxiproxy = new ToxiproxyClient(toxiHttp, "postgres");
        var prometheus = new PrometheusClient(promHttp);

        await ApiReadiness.WaitAsync(settings.Api, TimeSpan.FromMinutes(3), log, cancellationToken);
        await toxiproxy.EnsureProxyAsync(cancellationToken);
        await toxiproxy.ResetAsync(cancellationToken);

        var warmup = settings.Warmup.TotalSeconds;
        var faultEnd = warmup + settings.Fault.TotalSeconds;
        var total = settings.Warmup + settings.Fault + settings.Recovery;
        var schedule = settings.Experiment.Schedule(settings.Fault)
            .Select(a => a with { At = a.At + settings.Warmup })
            .OrderBy(a => a.At)
            .ToList();

        log.WriteLine($"[{settings.Experiment.Name}/{settings.Variant}] {settings.RatePerSecond} req/s for {total.TotalSeconds:0} s " +
                      $"(warm-up {warmup:0} s, fault {settings.Fault.TotalSeconds:0} s, recovery {settings.Recovery.TotalSeconds:0} s)");

        var generator = new LoadGenerator(settings.Api, settings.ClientTimeout);
        await Prewarm.RunAsync(generator, settings.Api, 500, log, cancellationToken);
        var startedAt = DateTimeOffset.UtcNow;
        var samples = await generator.RunAsync(
            settings.RatePerSecond,
            total,
            Path.Combine(settings.OutputDirectory, "nbomber", $"{settings.Experiment.Name}-{settings.Variant}"),
            (clock, ct) => ApplyScheduleAsync(toxiproxy, schedule, clock, ct),
            cancellationToken);
        await toxiproxy.ResetAsync(CancellationToken.None);

        // Let the last metric push (5 s) and two rule evaluations (5 s each) land before reading Prometheus back.
        await Task.Delay(TimeSpan.FromSeconds(20), cancellationToken);
        var endedAt = startedAt + total;

        var phases = new[]
        {
            new Phase("warmup", 0, warmup),
            new Phase("fault", warmup, faultEnd),
            new Phase("recovery", faultEnd, total.TotalSeconds),
        };

        var client = phases.Select(p => Analysis.Summarise(p, samples)).ToList();
        var server = new List<ServerPhaseStats>();
        foreach (var phase in phases)
        {
            server.Add(await ServerStatsAsync(prometheus, phase, startedAt, cancellationToken));
        }

        var serverWhole = await ServerStatsAsync(prometheus, new Phase("run", 0, total.TotalSeconds + 8), startedAt, cancellationToken);
        var serverBudget = SloCatalog.All.ToDictionary(
            s => s.Name,
            s =>
            {
                var compliance = s.Kind == SliKind.Availability ? serverWhole.Availability : serverWhole.LatencyCompliance[s.Name];
                var eligible = s.Kind == SliKind.Availability ? serverWhole.Requests : serverWhole.Requests - serverWhole.ServerErrors;
                var bad = (long)Math.Round((1 - compliance) * eligible);
                return SloMath.BudgetConsumed(bad, settings.RatePerSecond, s.Objective, s.Period);
            });

        var maxBurn5m = new Dictionary<string, double>(StringComparer.Ordinal);
        var maxBurn1h = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var slo in SloCatalog.All)
        {
            maxBurn5m[slo.Name] = await MaxAsync(prometheus, $"{PrometheusRuleGenerator.BurnRateRecord}5m{{slo=\"{slo.Name}\"}}", startedAt, endedAt, cancellationToken);
            maxBurn1h[slo.Name] = await MaxAsync(prometheus, $"{PrometheusRuleGenerator.BurnRateRecord}1h{{slo=\"{slo.Name}\"}}", startedAt, endedAt, cancellationToken);
        }

        var alerts = await AlertTimingsAsync(prometheus, startedAt, endedAt + TimeSpan.FromSeconds(15), warmup, faultEnd, cancellationToken);
        var modelled = SloCatalog.All.Where(s => s.IsAlertable)
            .SelectMany(s => BurnRateAlertPolicy.Tiers.Select(t => new ModelledAlert(
                PrometheusRuleGenerator.AlertName(s, t),
                AlertModel.FirstFiring(samples, s, t, settings.RatePerSecond, warmup, total.TotalSeconds))))
            .ToList();

        return new RunResult(
            settings.Experiment.Name,
            settings.Experiment.Title,
            settings.Experiment.Fault,
            settings.Variant,
            startedAt,
            settings.RatePerSecond,
            warmup,
            settings.Fault.TotalSeconds,
            settings.Recovery.TotalSeconds,
            settings.ClientTimeout.TotalSeconds,
            client,
            server,
            Analysis.BudgetConsumed(samples, settings.RatePerSecond),
            serverBudget,
            maxBurn5m,
            maxBurn1h,
            alerts,
            modelled,
            settings.Experiment.Schedule(settings.Fault).Count == 0 ? null : Analysis.RecoverySeconds(samples, faultEnd, total.TotalSeconds),
            $"{Environment.ProcessorCount} logical CPUs, {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, .NET {Environment.Version}");
    }

    private async Task ApplyScheduleAsync(ToxiproxyClient toxiproxy, IReadOnlyList<FaultAction> schedule, Stopwatch clock, CancellationToken cancellationToken)
    {
        foreach (var action in schedule)
        {
            var wait = action.At - clock.Elapsed;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, cancellationToken);
            }

            if (action.Add is not null)
            {
                await toxiproxy.AddAsync(action.Add, cancellationToken);
                log.WriteLine($"  t={clock.Elapsed.TotalSeconds,6:0.0}s  + {action.Add.Name}");
            }
            else if (action.Remove is not null)
            {
                await toxiproxy.RemoveAsync(action.Remove, cancellationToken);
                log.WriteLine($"  t={clock.Elapsed.TotalSeconds,6:0.0}s  - {action.Remove}");
            }
        }
    }

    private static async Task<ServerPhaseStats> ServerStatsAsync(PrometheusClient prometheus, Phase phase, DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        // Counter value at the phase end minus the value at its start, both read 7 s late (one 5 s push interval plus
        // margin). Exact up to the push granularity, with none of increase()'s extrapolation. A request is counted
        // when it completes, so requests that hang past a phase land in the next one, exactly as the alerts see them.
        var from = startedAt + TimeSpan.FromSeconds(phase.FromSeconds + 7);
        var to = startedAt + TimeSpan.FromSeconds(phase.ToSeconds + 7);
        var metric = SloCatalog.DurationMetric;
        var sel = SloCatalog.Selector;

        async Task<double> Delta(string selector)
        {
            var promql = $"sum({selector})";
            var end = await prometheus.ScalarAsync(promql, to, cancellationToken) ?? 0;
            var start = await prometheus.ScalarAsync(promql, from, cancellationToken) ?? 0;
            return Math.Max(0, end - start);
        }

        var total = await Delta($"{metric}_count{{{sel}}}");
        var errors = await Delta($"{metric}_count{{{sel},http_response_status_code=~\"5..\"}}");
        var closed = await Delta($"{metric}_count{{{sel},http_response_status_code=\"499\"}}");
        var latency = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var slo in SloCatalog.All.Where(s => s.Kind == SliKind.Latency))
        {
            var le = PrometheusRuleGenerator.Number(slo.ThresholdSeconds!.Value);
            var fast = await Delta($"{metric}_bucket{{{sel},http_response_status_code!~\"5..\",le=\"{le}\"}}");
            var ok = total - errors;
            latency[slo.Name] = ok <= 0 ? 1 : Math.Min(1, fast / ok);
        }

        return new ServerPhaseStats(phase.Name, Math.Round(total), Math.Round(errors), Math.Round(closed), total <= 0 ? 1 : 1 - (errors / total), latency);
    }


    private static async Task<double> MaxAsync(PrometheusClient prometheus, string promql, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        var series = await prometheus.RangeAsync(promql, from, to + TimeSpan.FromSeconds(15), PrometheusStep, cancellationToken);
        var values = series.SelectMany(s => s.Points).Select(p => p.Value).Where(v => !double.IsNaN(v) && !double.IsInfinity(v)).ToList();
        return values.Count == 0 ? 0 : values.Max();
    }

    private static async Task<IReadOnlyList<AlertTiming>> AlertTimingsAsync(
        PrometheusClient prometheus, DateTimeOffset from, DateTimeOffset to, double faultStart, double faultEnd, CancellationToken cancellationToken)
    {
        var series = await prometheus.RangeAsync("ALERTS{alertstate=\"firing\"}", from, to, PrometheusStep, cancellationToken);
        return [.. series
            .GroupBy(s => s.Labels.GetValueOrDefault("alertname", "?"))
            .Select(g =>
            {
                var times = g.SelectMany(s => s.Points).Select(p => (p.At - from).TotalSeconds).Order().ToList();
                return new AlertTiming(g.Key, Math.Round(times[0] - faultStart, 1), Math.Round(times[^1] - faultEnd, 1));
            })
            .OrderBy(a => a.Alert, StringComparer.Ordinal)];
    }

    internal static string Percent(double fraction) => (fraction * 100).ToString("0.###", CultureInfo.InvariantCulture) + "%";
}
