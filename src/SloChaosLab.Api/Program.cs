using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;
using SloChaosLab.Api.Features;
using SloChaosLab.Api.Infrastructure;
using SloChaosLab.Api.Ledger;
using SloChaosLab.Api.Persistence;

var builder = WebApplication.CreateBuilder(args);

// The lab profile is the lowest-priority configuration source: it fills in defaults, explicit keys still win.
builder.Configuration.Sources.Insert(0, new MemoryConfigurationSource { InitialData = LabProfile.Defaults(builder.Configuration["Lab:Profile"]) });

builder.AddObservability();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddPersistence(builder.Configuration);
builder.Services.Configure<LedgerOptions>(builder.Configuration.GetSection(LedgerOptions.Section));
builder.Services.Configure<ResilienceOptions>(builder.Configuration.GetSection(ResilienceOptions.Section));
builder.Services.AddSingleton<InteractivePostingStore>();
builder.Services.AddSingleton<FunctionPostingStore>();
builder.Services.AddSingleton<IPostingStore>(sp => sp.GetRequiredService<IOptions<LedgerOptions>>().Value.PostingMode switch
{
    PostingMode.Function => sp.GetRequiredService<FunctionPostingStore>(),
    _ => sp.GetRequiredService<InteractivePostingStore>(),
});
builder.Services.AddSingleton<PostingService>();

builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: [HealthEndpoints.ReadyTag]);

// RFC 9457 problem details for every error, with the trace id so a client-reported error can be found in traces.
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
    context.ProblemDetails.Extensions.TryAdd("traceId", Activity.Current?.TraceId.ToString() ?? context.HttpContext.TraceIdentifier));
builder.Services.AddExceptionHandler<DependencyExceptionHandler>();
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi("v1");

// Load shedding (experiment 3): beyond MaxConcurrentPostings in flight, answer 503 at once rather than queue work
// that will miss its deadline anyway. No queue: a queued request only waits to fail later.
var resilience = builder.Configuration.GetSection(ResilienceOptions.Section).Get<ResilienceOptions>() ?? new ResilienceOptions();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status503ServiceUnavailable;
    options.AddConcurrencyLimiter(LedgerEndpoints.PostingLimiter, limiter =>
    {
        limiter.PermitLimit = resilience.MaxConcurrentPostings;
        limiter.QueueLimit = 0;
        limiter.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
    });
    options.OnRejected = (context, _) =>
    {
        context.HttpContext.RequestServices.GetRequiredService<LedgerMetrics>().Outcome("shed");
        context.HttpContext.Response.Headers.RetryAfter = "1";
        return ValueTask.CompletedTask;
    };
});

var app = builder.Build();

if (args.Contains("--migrate-only"))
{
    await app.Services.MigrateDatabaseAsync();
    return;
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseRateLimiter();

if (app.Configuration.GetValue<bool>("OpenApi:Enabled"))
{
    app.MapOpenApi();
    app.MapScalarApiReference(options => options.WithTitle("slo-chaos-lab ledger API"));
}

app.MapHealthEndpoints();
app.MapLedgerEndpoints(shedLoad: resilience.Enabled);

// What is under test, for the experiment write-ups (no secrets: connection strings are not echoed).
app.MapGet("/api/v1/lab", (IOptions<LedgerOptions> ledger, IConfiguration config) => TypedResults.Ok(new LabInfo(
    config["Lab:Profile"] ?? LabProfile.Baseline,
    ledger.Value.PostingMode.ToString(),
    resilience.Enabled,
    config["Database:MaxPoolSize"] ?? "100 (Npgsql default)",
    config["Database:ConnectTimeoutSeconds"] ?? "15 (Npgsql default)",
    config["Database:CommandTimeoutSeconds"] ?? "30 (Npgsql default)",
    string.Create(CultureInfo.InvariantCulture, $".NET {Environment.Version}, {Environment.ProcessorCount} CPUs"))))
    .WithTags("Lab")
    .ExcludeFromDescription();

if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    await app.Services.MigrateDatabaseAsync();
}

await app.RunAsync();

/// <summary>Entry point; public so WebApplicationFactory can boot the app in integration tests.</summary>
public partial class Program;

internal sealed record LabInfo(
    string Profile, string PostingMode, bool Resilience, string MaxPoolSize, string ConnectTimeoutSeconds, string CommandTimeoutSeconds, string Runtime);
