using System.Diagnostics;
using System.Text.Json.Serialization;
using Scalar.AspNetCore;
using SloChaosLab.Api.Features;
using SloChaosLab.Api.Infrastructure;
using SloChaosLab.Api.Ledger;
using SloChaosLab.Api.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.AddObservability();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddPersistence(builder.Configuration);
builder.Services.Configure<LedgerOptions>(builder.Configuration.GetSection(LedgerOptions.Section));
builder.Services.AddSingleton<IPostingStore, InteractivePostingStore>();
builder.Services.AddSingleton<PostingService>();

builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: [HealthEndpoints.ReadyTag]);

// RFC 9457 problem details for every error, with the trace id so a client-reported error can be found in traces.
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
    context.ProblemDetails.Extensions.TryAdd("traceId", Activity.Current?.TraceId.ToString() ?? context.HttpContext.TraceIdentifier));
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi("v1");

var app = builder.Build();

if (args.Contains("--migrate-only"))
{
    await app.Services.MigrateDatabaseAsync();
    return;
}

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Configuration.GetValue<bool>("OpenApi:Enabled"))
{
    app.MapOpenApi();
    app.MapScalarApiReference(options => options.WithTitle("slo-chaos-lab ledger API"));
}

app.MapHealthEndpoints();
app.MapLedgerEndpoints();

if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    await app.Services.MigrateDatabaseAsync();
}

await app.RunAsync();

/// <summary>Entry point; public so WebApplicationFactory can boot the app in integration tests.</summary>
public partial class Program;
