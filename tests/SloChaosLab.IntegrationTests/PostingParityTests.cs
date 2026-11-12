using System.Net;
using System.Net.Http.Json;
using Npgsql;
using SloChaosLab.IntegrationTests.Infrastructure;
using static SloChaosLab.IntegrationTests.Infrastructure.Requests;

namespace SloChaosLab.IntegrationTests;

/// <summary>
/// The fix for experiment 1 replaced a four-round-trip client transaction with a PL/pgSQL function. These tests run
/// the same cases against both posting modes: the fix may change the latency, never the ledger's behaviour.
/// </summary>
public sealed class PostingParityTests(LabFixture lab)
{
    public static TheoryData<string> Modes => ["Interactive", "Function"];

    private ApiFactory Factory(string mode) => new(lab.AppConnectionString, "baseline", mode);

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task Posts_once_and_moves_both_balances(string mode)
    {
        await using var factory = Factory(mode);
        using var client = factory.CreateClient();
        var (debit, credit) = Accounts();
        var before = await client.GetFromJsonAsync<AccountDto>($"/api/v1/accounts/{debit}", TestContext.Current.CancellationToken);

        using var response = await client.SendAsync(Post(NewKey(), Transfer(debit, credit, 12.34m)), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.GetValues("Idempotent-Replayed").Should().Equal("false");
        var after = await client.GetFromJsonAsync<AccountDto>($"/api/v1/accounts/{debit}", TestContext.Current.CancellationToken);
        (after!.Balance - before!.Balance).Should().Be(12.34m);
        after.Version.Should().Be(before.Version + 1);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task Same_key_and_payload_replays_the_original_entry(string mode)
    {
        await using var factory = Factory(mode);
        using var client = factory.CreateClient();
        var (debit, credit) = Accounts();
        var key = NewKey();

        using var first = await client.SendAsync(Post(key, Transfer(debit, credit, 5m)), TestContext.Current.CancellationToken);
        using var second = await client.SendAsync(Post(key, Transfer(debit, credit, 5m)), TestContext.Current.CancellationToken);

        second.StatusCode.Should().Be(HttpStatusCode.Created);
        second.Headers.GetValues("Idempotent-Replayed").Should().Equal("true");
        var a = await first.Content.ReadFromJsonAsync<EntryDto>(TestContext.Current.CancellationToken);
        var b = await second.Content.ReadFromJsonAsync<EntryDto>(TestContext.Current.CancellationToken);
        b!.Id.Should().Be(a!.Id);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task Same_key_with_a_different_payload_is_rejected(string mode)
    {
        await using var factory = Factory(mode);
        using var client = factory.CreateClient();
        var (debit, credit) = Accounts();
        var key = NewKey();

        using var first = await client.SendAsync(Post(key, Transfer(debit, credit, 5m)), TestContext.Current.CancellationToken);
        using var second = await client.SendAsync(Post(key, Transfer(debit, credit, 6m)), TestContext.Current.CancellationToken);

        second.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await second.Content.ReadFromJsonAsync<ProblemDto>(TestContext.Current.CancellationToken))!.Code.Should().Be("idempotency.key_reused");
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task Unknown_accounts_are_rejected_and_the_key_stays_usable(string mode)
    {
        await using var factory = Factory(mode);
        using var client = factory.CreateClient();
        var key = NewKey();

        using var rejected = await client.SendAsync(Post(key, Transfer("CUST-00001", "NOPE-1", 1m)), TestContext.Current.CancellationToken);
        rejected.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await rejected.Content.ReadFromJsonAsync<ProblemDto>(TestContext.Current.CancellationToken))!.Code.Should().Be("entry.unknown_account");

        // The rejection rolled back the idempotency claim: the same key now posts a corrected request.
        var (debit, credit) = Accounts();
        using var corrected = await client.SendAsync(Post(key, Transfer(debit, credit, 1m)), TestContext.Current.CancellationToken);
        corrected.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task Concurrent_requests_with_one_key_post_exactly_one_entry(string mode)
    {
        await using var factory = Factory(mode);
        using var client = factory.CreateClient();
        var (debit, credit) = Accounts();
        var key = NewKey();

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            client.SendAsync(Post(key, Transfer(debit, credit, 3m)), TestContext.Current.CancellationToken)));

        responses.Should().AllSatisfy(r => r.StatusCode.Should().Be(HttpStatusCode.Created));
        var ids = await Task.WhenAll(responses.Select(r => r.Content.ReadFromJsonAsync<EntryDto>(TestContext.Current.CancellationToken)));
        ids.Select(e => e!.Id).Distinct().Should().ContainSingle();
        responses.Count(r => r.Headers.GetValues("Idempotent-Replayed").Single() == "false").Should().Be(1);
        foreach (var r in responses)
        {
            r.Dispose();
        }
    }

    [Fact]
    public async Task Ledger_invariants_hold_across_everything_both_modes_wrote()
    {
        await using var dataSource = NpgsqlDataSource.Create(lab.AppConnectionString);
        await using var drift = dataSource.CreateCommand("SELECT count(*) FROM ledger.account_balance_drift");
        ((long)(await drift.ExecuteScalarAsync(TestContext.Current.CancellationToken))!).Should().Be(0);
        await using var net = dataSource.CreateCommand("SELECT coalesce(sum(balance_minor), 0)::bigint FROM ledger.accounts");
        ((long)(await net.ExecuteScalarAsync(TestContext.Current.CancellationToken))!).Should().Be(0, "every entry is balanced, so all balances net to zero");
    }

    [Fact]
    public async Task The_application_role_cannot_rewrite_history()
    {
        await using var dataSource = NpgsqlDataSource.Create(lab.AppConnectionString);
        await using var cmd = dataSource.CreateCommand("DELETE FROM ledger.postings");
        var act = async () => await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }
}
