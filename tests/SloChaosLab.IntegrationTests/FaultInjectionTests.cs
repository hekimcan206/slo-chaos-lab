using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Npgsql;
using SloChaosLab.IntegrationTests.Infrastructure;
using static SloChaosLab.IntegrationTests.Infrastructure.Requests;

namespace SloChaosLab.IntegrationTests;

/// <summary>
/// The postmortem fixes, pinned as tests against a real Toxiproxy link: the behaviour each experiment measured at
/// load, reproduced here with single requests so a regression fails the build rather than the next game day.
/// </summary>
public sealed class FaultInjectionTests(LabFixture lab) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await lab.ResetAsync();

    public async ValueTask DisposeAsync() => await lab.ResetAsync();

    [Fact]
    public async Task Hardened_service_answers_503_instead_of_hanging_during_a_partition()
    {
        await using var factory = new ApiFactory(lab.ProxiedAppConnectionString, "hardened");
        using var client = factory.CreateClient();
        await WarmAsync(client);
        await BlackholeAsync();

        var sw = Stopwatch.StartNew();
        var (debit, credit) = Accounts();
        using var response = await client.SendAsync(Post(NewKey(), Transfer(debit, credit, 1m)), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        // Known gap (postmortem 5): the first request on a black-holed pooled connection ends only when Npgsql's
        // 2 s command timeout and connection teardown finish, about 4 s, because Polly's deadline is cooperative.
        // A hard deadline (WaitAsync) was tried and reverted: under load it kept the breaker from opening.
        // The baseline would hold the request for up to 30 s; once the breaker opens, failures take about 1 ms.
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(6));
        (await response.Content.ReadFromJsonAsync<ProblemDto>(TestContext.Current.CancellationToken))!.Code.Should().StartWith("dependency.");
    }

    [Fact]
    public async Task Baseline_service_is_still_working_on_the_request_when_a_2s_caller_gives_up()
    {
        // Experiment 5's weakness: with Npgsql defaults the request outlives its caller, so the server records 499,
        // not 5xx, and the availability SLI never sees the outage.
        await using var factory = new ApiFactory(lab.ProxiedAppConnectionString, "baseline");
        using var client = factory.CreateClient();
        await WarmAsync(client);
        await BlackholeAsync();

        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var (debit, credit) = Accounts();
        var act = async () => await client.SendAsync(Post(NewKey(), Transfer(debit, credit, 1m)), caller.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Breaker_opens_under_sustained_failure_and_then_fails_fast_with_retry_after()
    {
        await using var factory = new ApiFactory(lab.ProxiedAppConnectionString, "hardened");
        using var client = factory.CreateClient();
        await WarmAsync(client);
        await lab.AddToxicAsync("reset", "reset_peer", "upstream", new { timeout = 0 });

        // Enough failing attempts inside the 10 s sampling window to cross the minimum throughput of 20.
        for (var i = 0; i < 12; i++)
        {
            var (d, c) = Accounts();
            using var _ = await client.SendAsync(Post(NewKey(), Transfer(d, c, 1m)), TestContext.Current.CancellationToken);
        }

        var sw = Stopwatch.StartNew();
        var (debit, credit) = Accounts();
        using var response = await client.SendAsync(Post(NewKey(), Transfer(debit, credit, 1m)), TestContext.Current.CancellationToken);
        sw.Stop();

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadFromJsonAsync<ProblemDto>(TestContext.Current.CancellationToken))!.Code.Should().Be("dependency.circuit_open");
        response.Headers.RetryAfter.Should().NotBeNull();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(250), "an open breaker does not touch the network");
    }

    [Fact]
    public async Task Retries_ride_out_a_short_reset_burst_and_post_exactly_once()
    {
        await using var factory = new ApiFactory(lab.ProxiedAppConnectionString, "hardened");
        using var client = factory.CreateClient();
        await WarmAsync(client);
        await lab.AddToxicAsync("reset", "reset_peer", "upstream", new { timeout = 0 });
        var heal = Task.Run(async () =>
        {
            await Task.Delay(150, TestContext.Current.CancellationToken);
            await lab.ResetAsync();
        }, TestContext.Current.CancellationToken);

        var key = NewKey();
        var (debit, credit) = Accounts();
        using var response = await client.SendAsync(Post(key, Transfer(debit, credit, 2m)), TestContext.Current.CancellationToken);
        await heal;

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        await using var dataSource = NpgsqlDataSource.Create(lab.AppConnectionString);
        await using var count = dataSource.CreateCommand("SELECT count(*) FROM ledger.journal_entries WHERE idempotency_key = $1");
        count.Parameters.Add(new() { Value = key });
        ((long)(await count.ExecuteScalarAsync(TestContext.Current.CancellationToken))!).Should().Be(1);
    }

    [Fact]
    public async Task Readiness_reports_unhealthy_within_its_own_deadline_during_a_partition()
    {
        await using var factory = new ApiFactory(lab.ProxiedAppConnectionString, "hardened");
        using var client = factory.CreateClient();
        await WarmAsync(client);
        await BlackholeAsync();

        var sw = Stopwatch.StartNew();
        using var response = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(4));
        (await client.GetAsync("/health/live", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task BlackholeAsync()
    {
        await lab.AddToxicAsync("blackhole-up", "timeout", "upstream", new { timeout = 0 });
        await lab.AddToxicAsync("blackhole-down", "timeout", "downstream", new { timeout = 0 });
    }

    private static async Task WarmAsync(HttpClient client)
    {
        // Open pooled connections while the link is healthy, as in the experiments' warm-up.
        var (debit, credit) = Accounts();
        using var response = await client.SendAsync(Post(NewKey(), Transfer(debit, credit, 1m)), TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }
}
