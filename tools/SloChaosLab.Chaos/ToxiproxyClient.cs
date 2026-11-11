using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace SloChaosLab.Chaos;

/// <summary>A toxic as the Toxiproxy HTTP API takes it (https://github.com/Shopify/toxiproxy#toxics).</summary>
/// <param name="Stream">"upstream" is client to server (queries), "downstream" is server to client (results).</param>
/// <param name="Toxicity">Probability that the toxic applies to a given connection.</param>
internal sealed record Toxic(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("stream")] string Stream,
    [property: JsonPropertyName("toxicity")] double Toxicity,
    [property: JsonPropertyName("attributes")] IReadOnlyDictionary<string, int> Attributes);

/// <summary>Minimal client for the Toxiproxy admin API: add and remove toxics, reset to a clean state.</summary>
internal sealed class ToxiproxyClient(HttpClient http, string proxy)
{
    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        // POST /reset re-enables every proxy and removes every toxic: experiments always start from a clean link.
        using var response = await http.PostAsync(new Uri("reset", UriKind.Relative), null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task AddAsync(Toxic toxic, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync(new Uri($"proxies/{proxy}/toxics", UriKind.Relative), toxic, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Toxiproxy rejected toxic {toxic.Name}: {(int)response.StatusCode} {body}");
        }
    }

    public async Task RemoveAsync(string toxicName, CancellationToken cancellationToken)
    {
        using var response = await http.DeleteAsync(new Uri($"proxies/{proxy}/toxics/{toxicName}", UriKind.Relative), cancellationToken);
        // 404: already gone (e.g. a reset raced the removal); the goal state is reached either way.
        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
        {
            response.EnsureSuccessStatusCode();
        }
    }

    public async Task EnsureProxyAsync(CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(new Uri($"proxies/{proxy}", UriKind.Relative), cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Toxiproxy has no proxy named '{proxy}' ({(int)response.StatusCode}). Is deploy/toxiproxy/toxiproxy.json loaded?");
        }
    }
}
