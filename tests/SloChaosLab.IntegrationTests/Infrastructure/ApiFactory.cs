using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace SloChaosLab.IntegrationTests.Infrastructure;

/// <summary>
/// Boots the real application in memory. Only configuration changes (connection string, lab profile, posting mode);
/// no service is replaced, so the tests exercise the production composition root.
/// </summary>
public sealed class ApiFactory(string connectionString, string profile, string? postingMode = null) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", connectionString);
        builder.UseSetting("Lab:Profile", profile);
        if (postingMode is not null)
        {
            builder.UseSetting("Ledger:PostingMode", postingMode);
        }

        builder.UseSetting("Database:MigrateOnStartup", "false");
        builder.UseSetting("Logging:LogLevel:Default", "Warning");
    }
}

public static class Requests
{
    private static int _account;

    /// <summary>Two distinct synthetic accounts per call, rotating through the 1,000 seeded ones.</summary>
    public static (string Debit, string Credit) Accounts()
    {
        var n = Interlocked.Add(ref _account, 2);
        return ($"CUST-{(n % 1000) + 1:D5}", $"CUST-{((n + 1) % 1000) + 1:D5}");
    }

    public static object Transfer(string debit, string credit, decimal amount) => new
    {
        description = "integration transfer",
        valueDate = "2026-11-12",
        currency = "TRY",
        lines = new object[]
        {
            new { accountCode = debit, direction = "debit", amount },
            new { accountCode = credit, direction = "credit", amount },
        },
    };

    public static HttpRequestMessage Post(string key, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/journal-entries") { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", key);
        return request;
    }

    public static string NewKey() => Guid.NewGuid().ToString("N");

    public sealed record EntryDto(Guid Id, string IdempotencyKey);

    public sealed record ProblemDto(int Status, string? Code, string? Detail);

    public sealed record AccountDto(string Code, decimal Balance, long Version);
}
