namespace SloChaosLab.Chaos;

internal static class ApiReadiness
{
    /// <summary>Polls /health/ready until the service answers 200: compose starts containers, not ready services.</summary>
    public static async Task WaitAsync(Uri api, TimeSpan timeout, TextWriter log, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { BaseAddress = api, Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            try
            {
                using var response = await http.GetAsync(new Uri("health/ready", UriKind.Relative), cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    log.WriteLine($"service ready at {api}");
                    return;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                // not listening yet
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException($"{api} was not ready within {timeout.TotalSeconds:0} s.");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }
}
