using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using SloChaosLab.Chaos;
using SloChaosLab.Slo;

// slo-chaos-lab experiment runner. Commands:
//   generate-rules [--out deploy/prometheus/rules/slo-rules.yml]
//   run --experiment <baseline|latency|jitter|bandwidth|reset|partition> --variant <before|after> [options]
//   capacity [--rates 50,100,200,400] [--step 60]
//   smoke [--alert LedgerPostAvailabilityFastBurn] [--deadline 240]
//   report [--results docs/results]
// Common options: --api, --toxiproxy, --prometheus, --rate, --warmup, --fault, --recovery, --client-timeout, --out
var options = Options.Parse(args.Skip(1).ToArray());
var command = args.FirstOrDefault() ?? "help";
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    WriteIndented = true,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
};

switch (command)
{
    case "generate-rules":
        {
            var path = options.Get("out", "deploy/prometheus/rules/slo-rules.yml");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            await File.WriteAllTextAsync(path, PrometheusRuleGenerator.Generate(SloCatalog.All));
            Console.WriteLine($"wrote {path}");
            return 0;
        }

    case "run":
        {
            var settings = options.Settings(ExperimentCatalog.Find(options.Get("experiment", "baseline")), options.Get("variant", "before"));
            Directory.CreateDirectory(settings.OutputDirectory);
            var result = await new ExperimentRunner(settings, Console.Out).RunAsync(cts.Token);
            var path = Path.Combine(settings.OutputDirectory, $"{result.Experiment}-{result.Variant}.json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(result, json));
            Console.WriteLine(Report.Single(result));
            Console.WriteLine($"wrote {path}");
            return 0;
        }

    case "capacity":
        {
            var settings = options.Settings(ExperimentCatalog.None, "capacity");
            var rates = options.Get("rates", "50,100,200,400").Split(',').Select(r => int.Parse(r, CultureInfo.InvariantCulture)).ToArray();
            var step = TimeSpan.FromSeconds(options.GetInt("step", 60));
            var result = await Capacity.RunAsync(settings, rates, step, Console.Out, cts.Token);
            Directory.CreateDirectory(settings.OutputDirectory);
            var path = Path.Combine(settings.OutputDirectory, "capacity.json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(result, json));
            Console.WriteLine(Report.CapacityTable(result));
            Console.WriteLine($"wrote {path}");
            return 0;
        }

    case "smoke":
        {
            var settings = options.Settings(ExperimentCatalog.Partition, "smoke");
            var alert = options.Get("alert", PrometheusRuleGenerator.AlertName(SloCatalog.Availability, BurnRateAlertPolicy.Tiers[0]));
            var deadline = TimeSpan.FromSeconds(options.GetInt("deadline", 240));
            return await Smoke.RunAsync(settings, alert, deadline, Console.Out, cts.Token) ? 0 : 1;
        }

    case "report":
        {
            var dir = options.Get("results", "docs/results");
            var results = Directory.GetFiles(dir, "*-*.json")
                .Where(f => !f.EndsWith("capacity.json", StringComparison.Ordinal))
                .Select(f => JsonSerializer.Deserialize<RunResult>(File.ReadAllText(f), json)!)
                .ToList();
            var path = Path.Combine(dir, "summary.md");
            await File.WriteAllTextAsync(path, Report.Summary(results));
            Console.WriteLine($"wrote {path} ({results.Count} runs)");
            return 0;
        }

    default:
        Console.Error.WriteLine("usage: chaos <generate-rules|run|capacity|smoke|report> [--option value ...]  (see the comment at the top of Program.cs)");
        return command == "help" ? 0 : 2;
}

/// <summary>--key value pairs with defaults that match docker-compose.yml.</summary>
internal sealed class Options(Dictionary<string, string> values)
{
    public static Options Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
            {
                throw new ArgumentException($"Expected '--name value', got '{args[i]}'.");
            }

            values[args[i][2..]] = args[++i];
        }

        return new Options(values);
    }

    public string Get(string key, string fallback) => values.TryGetValue(key, out var v) ? v : fallback;

    public int GetInt(string key, int fallback) => values.TryGetValue(key, out var v) ? int.Parse(v, CultureInfo.InvariantCulture) : fallback;

    public RunSettings Settings(Experiment experiment, string variant) => new(
        experiment,
        variant,
        GetInt("rate", 50),
        TimeSpan.FromSeconds(GetInt("warmup", 60)),
        TimeSpan.FromSeconds(GetInt("fault", 120)),
        TimeSpan.FromSeconds(GetInt("recovery", 90)),
        TimeSpan.FromSeconds(GetInt("client-timeout", 2)),
        new Uri(Get("api", "http://localhost:58380/")),
        new Uri(Get("toxiproxy", "http://localhost:58474/")),
        new Uri(Get("prometheus", "http://localhost:58390/")),
        Get("out", "docs/results"));
}
