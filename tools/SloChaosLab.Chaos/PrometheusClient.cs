using System.Globalization;
using System.Text.Json;

namespace SloChaosLab.Chaos;

internal sealed record Series(IReadOnlyDictionary<string, string> Labels, IReadOnlyList<(DateTimeOffset At, double Value)> Points);

/// <summary>Read-only client for the Prometheus HTTP API (instant and range queries, active alerts).</summary>
internal sealed class PrometheusClient(HttpClient http)
{
    public async Task<IReadOnlyList<Series>> QueryAsync(string promql, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var url = $"api/v1/query?query={Uri.EscapeDataString(promql)}&time={Unix(at)}";
        return await GetAsync(url, cancellationToken);
    }

    public async Task<double?> ScalarAsync(string promql, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var result = await QueryAsync(promql, at, cancellationToken);
        return result.Count == 0 || result[0].Points.Count == 0 || double.IsNaN(result[0].Points[0].Value) ? null : result[0].Points[0].Value;
    }

    public async Task<IReadOnlyList<Series>> RangeAsync(string promql, DateTimeOffset from, DateTimeOffset to, TimeSpan step, CancellationToken cancellationToken)
    {
        var url = $"api/v1/query_range?query={Uri.EscapeDataString(promql)}&start={Unix(from)}&end={Unix(to)}&step={step.TotalSeconds.ToString(CultureInfo.InvariantCulture)}";
        return await GetAsync(url, cancellationToken);
    }

    /// <summary>Names of alerts currently in the firing state.</summary>
    public async Task<IReadOnlyList<string>> FiringAlertsAsync(CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(new Uri("api/v1/alerts", UriKind.Relative), cancellationToken);
        response.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return [.. doc.RootElement.GetProperty("data").GetProperty("alerts").EnumerateArray()
            .Where(a => a.GetProperty("state").GetString() == "firing")
            .Select(a => a.GetProperty("labels").GetProperty("alertname").GetString()!)
            .Distinct()];
    }

    private async Task<IReadOnlyList<Series>> GetAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(new Uri(url, UriKind.Relative), cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Prometheus query failed ({(int)response.StatusCode}): {body}");
        }

        using var doc = JsonDocument.Parse(body);
        var data = doc.RootElement.GetProperty("data");
        var list = new List<Series>();
        foreach (var item in data.GetProperty("result").EnumerateArray())
        {
            var labels = item.GetProperty("metric").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty);
            var points = new List<(DateTimeOffset, double)>();
            if (item.TryGetProperty("value", out var value))
            {
                points.Add(Point(value));
            }
            else
            {
                points.AddRange(item.GetProperty("values").EnumerateArray().Select(Point));
            }

            list.Add(new Series(labels, points));
        }

        return list;
    }

    private static (DateTimeOffset, double) Point(JsonElement pair) =>
        (DateTimeOffset.FromUnixTimeMilliseconds((long)(pair[0].GetDouble() * 1000)),
         ParseSample(pair[1].GetString()!));

    /// <summary>Prometheus encodes sample values as strings, including "NaN", "+Inf" and "-Inf".</summary>
    internal static double ParseSample(string value) => value switch
    {
        "+Inf" => double.PositiveInfinity,
        "-Inf" => double.NegativeInfinity,
        "NaN" => double.NaN,
        _ => double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture),
    };

    private static string Unix(DateTimeOffset at) => (at.ToUnixTimeMilliseconds() / 1000.0).ToString("0.000", CultureInfo.InvariantCulture);
}
