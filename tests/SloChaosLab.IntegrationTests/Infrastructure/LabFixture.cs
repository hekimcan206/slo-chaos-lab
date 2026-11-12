using System.Net.Http.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SloChaosLab.Api.Persistence;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(SloChaosLab.IntegrationTests.Infrastructure.LabFixture))]

namespace SloChaosLab.IntegrationTests.Infrastructure;

/// <summary>
/// The lab topology in miniature, for the whole assembly: PostgreSQL 17 (initialised with the compose role script,
/// migrated as <c>ledger_migrator</c>) and Toxiproxy in front of it on a private Docker network. The API under test
/// connects as <c>ledger_app</c> either directly or through the proxy, so fault-injection tests break the same link
/// the experiments break.
/// </summary>
public sealed class LabFixture : IAsyncLifetime
{
    // Test-only credentials for throwaway containers.
    private const string MigratorPassword = "migrator-test";
    private const string AppPassword = "app-test";
    private const int ProxyPort = 15432;

    private readonly INetwork _network = new NetworkBuilder().Build();
    private readonly PostgreSqlContainer _postgres;
    private readonly IContainer _toxiproxy;
    private HttpClient? _toxiproxyApi;

    public LabFixture()
    {
        _postgres = new PostgreSqlBuilder("postgres:17-alpine")
            .WithNetwork(_network)
            .WithNetworkAliases("postgres")
            .WithDatabase("ledger")
            .WithEnvironment("MIGRATOR_PASSWORD", MigratorPassword)
            .WithEnvironment("APP_PASSWORD", AppPassword)
            .WithCommand("-c", "max_connections=200")
            .WithResourceMapping(
                File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "postgres-init", "01-roles.sh")),
                "/docker-entrypoint-initdb.d/01-roles.sh")
            .Build();

        _toxiproxy = new ContainerBuilder("ghcr.io/shopify/toxiproxy:2.12.0")
            .WithNetwork(_network)
            .WithCommand("-host=0.0.0.0")
            .WithPortBinding(8474, assignRandomHostPort: true)
            .WithPortBinding(ProxyPort, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8474).ForPath("/version")))
            .Build();
    }

    public string MigratorConnectionString => Direct("ledger_migrator", MigratorPassword);

    /// <summary>Application role, straight to PostgreSQL.</summary>
    public string AppConnectionString => Direct("ledger_app", AppPassword);

    /// <summary>Application role through Toxiproxy (the request path of the lab).</summary>
    public string ProxiedAppConnectionString => new NpgsqlConnectionStringBuilder(AppConnectionString)
    {
        Host = _toxiproxy.Hostname,
        Port = _toxiproxy.GetMappedPublicPort(ProxyPort),
    }.ConnectionString;

    public HttpClient Toxiproxy => _toxiproxyApi ?? throw new InvalidOperationException("Fixture not initialised.");

    public async ValueTask InitializeAsync()
    {
        await _network.CreateAsync();
        await Task.WhenAll(_postgres.StartAsync(), _toxiproxy.StartAsync());
        await using (var dataSource = PersistenceExtensions.CreateDataSource(MigratorConnectionString))
        {
            await new MigrationRunner(dataSource, NullLogger<MigrationRunner>.Instance).MigrateAsync();
        }

        _toxiproxyApi = new HttpClient { BaseAddress = new Uri($"http://{_toxiproxy.Hostname}:{_toxiproxy.GetMappedPublicPort(8474)}/") };
        using var created = await _toxiproxyApi.PostAsJsonAsync(
            "proxies", new { name = "postgres", listen = $"0.0.0.0:{ProxyPort}", upstream = "postgres:5432", enabled = true });
        created.EnsureSuccessStatusCode();
    }

    public async Task AddToxicAsync(string name, string type, string stream, object attributes)
    {
        using var response = await Toxiproxy.PostAsJsonAsync("proxies/postgres/toxics", new { name, type, stream, toxicity = 1.0, attributes });
        response.EnsureSuccessStatusCode();
    }

    public async Task ResetAsync()
    {
        using var response = await Toxiproxy.PostAsync("reset", null);
        response.EnsureSuccessStatusCode();
    }

    public async ValueTask DisposeAsync()
    {
        _toxiproxyApi?.Dispose();
        await _toxiproxy.DisposeAsync();
        await _postgres.DisposeAsync();
        await _network.DisposeAsync();
    }

    private string Direct(string username, string password) =>
        new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString())
        {
            Username = username,
            Password = password,
            // Generous connect timeout on direct connections: a loaded laptop or CI runner forks backends slowly.
            Timeout = 30,
        }.ConnectionString;
}
