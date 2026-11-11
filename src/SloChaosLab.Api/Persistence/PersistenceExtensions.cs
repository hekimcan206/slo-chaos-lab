using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace SloChaosLab.Api.Persistence;

internal static class PersistenceExtensions
{
    /// <summary>
    /// Two connection strings on purpose. <c>Default</c> is the request path: the application role, routed through
    /// Toxiproxy in the lab, so every fault lands exactly where a real network fault would. <c>Migrator</c> is the
    /// schema owner and goes straight to PostgreSQL, so start-up migrations never depend on the fault under test.
    /// </summary>
    public static IServiceCollection AddPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Connection string 'Default' is not configured.");

        services.AddSingleton(_ => CreateDataSource(connectionString));
        services.AddSingleton(sp =>
        {
            var migrator = configuration.GetConnectionString("Migrator");
            // Without a dedicated migrator connection (tests, local runs) the request-path data source is used.
            return new MigrationRunner(
                string.IsNullOrWhiteSpace(migrator) ? sp.GetRequiredService<NpgsqlDataSource>() : CreateDataSource(migrator),
                sp.GetRequiredService<ILogger<MigrationRunner>>());
        });
        return services;
    }

    public static NpgsqlDataSource CreateDataSource(string connectionString)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);

        // Carried over from ledger-recon: a request never queues forever behind a row lock, and an abandoned
        // transaction cannot pin locks. These are server-side guards; client-side timeouts are a separate decision
        // (docs/adr/0004) because they are what the chaos experiments exercise.
        builder.ConnectionStringBuilder.Options ??= "-c lock_timeout=5000 -c idle_in_transaction_session_timeout=30000";

        // No Kerberos here; skipping GSS negotiation also stops Npgsql probing for libgssapi, which chiseled images lack.
        builder.ConnectionStringBuilder.GssEncryptionMode = GssEncryptionMode.Disable;
        return builder.Build();
    }

    public static async Task MigrateDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default) =>
        await services.GetRequiredService<MigrationRunner>().MigrateAsync(cancellationToken);
}

/// <summary>Readiness: can we open a connection and run a trivial query within the probe deadline?</summary>
internal sealed class DatabaseHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            // A probe must answer even while the database hangs, so it carries its own short deadline.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(2));
            await using var cmd = dataSource.CreateCommand("SELECT 1");
            await cmd.ExecuteScalarAsync(deadline.Token);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is NpgsqlException or OperationCanceledException or TimeoutException)
        {
            return HealthCheckResult.Unhealthy("Database unavailable.", ex);
        }
    }
}
