// Local orchestration with .NET Aspire: `dotnet run --project src/SloChaosLab.AppHost`.
// Same topology as docker-compose.yml (which CI and the recorded experiments use), with the ledger API running as a
// local process under the debugger and its traces, logs and metrics in the Aspire dashboard. Requests reach
// PostgreSQL through Toxiproxy; migrations go direct. Ports match compose, so the chaos runner works against both
// (run one of the two at a time).
var builder = DistributedApplication.CreateBuilder(args);

var profile = builder.Configuration["LabProfile"] ?? "hardened";
const string AppPassword = "app-dev-only";       // local lab credentials, same defaults as compose / .env.example
const string MigratorPassword = "migrator-dev-only";

var postgres = builder.AddContainer("postgres", "postgres", "17-alpine")
    .WithEnvironment("POSTGRES_DB", "ledger")
    .WithEnvironment("POSTGRES_USER", "postgres")
    .WithEnvironment("POSTGRES_PASSWORD", "postgres-dev-only")
    .WithEnvironment("APP_PASSWORD", AppPassword)
    .WithEnvironment("MIGRATOR_PASSWORD", MigratorPassword)
    .WithArgs("postgres", "-c", "max_connections=200")
    .WithBindMount("../../deploy/postgres/init", "/docker-entrypoint-initdb.d", isReadOnly: true)
    .WithEndpoint(port: 58432, targetPort: 5432, scheme: "tcp", name: "tcp");

var toxiproxy = builder.AddContainer("toxiproxy", "ghcr.io/shopify/toxiproxy", "2.12.0")
    .WithArgs("-host=0.0.0.0", "-config=/config/toxiproxy.json")
    .WithBindMount("../../deploy/toxiproxy", "/config", isReadOnly: true)
    .WithHttpEndpoint(port: 58474, targetPort: 8474, name: "admin")
    .WithEndpoint(port: 58415, targetPort: 15432, scheme: "tcp", name: "postgres")
    .WaitFor(postgres);

var prometheus = builder.AddContainer("prometheus", "prom/prometheus", "v3.15.1")
    .WithArgs("--config.file=/etc/prometheus/prometheus.yml", "--web.enable-otlp-receiver", "--storage.tsdb.path=/prometheus")
    .WithBindMount("../../deploy/prometheus", "/etc/prometheus", isReadOnly: true)
    .WithHttpEndpoint(port: 58390, targetPort: 9090, name: "http");

builder.AddContainer("grafana", "grafana/grafana", "13.2.6")
    .WithEnvironment("GF_AUTH_ANONYMOUS_ENABLED", "true")
    .WithEnvironment("GF_AUTH_ANONYMOUS_ORG_ROLE", "Viewer")
    .WithEnvironment("GF_DASHBOARDS_DEFAULT_HOME_DASHBOARD_PATH", "/etc/grafana/dashboards/slo.json")
    .WithBindMount("../../deploy/grafana/provisioning", "/etc/grafana/provisioning", isReadOnly: true)
    .WithBindMount("../../deploy/grafana/dashboards", "/etc/grafana/dashboards", isReadOnly: true)
    .WithHttpEndpoint(port: 58300, targetPort: 3000, name: "http")
    .WaitFor(prometheus);

builder.AddProject<Projects.SloChaosLab_Api>("ledger-api")
    .WithEnvironment("ConnectionStrings__Default", $"Host=localhost;Port=58415;Database=ledger;Username=ledger_app;Password={AppPassword}")
    .WithEnvironment("ConnectionStrings__Migrator", $"Host=localhost;Port=58432;Database=ledger;Username=ledger_migrator;Password={MigratorPassword}")
    .WithEnvironment("Database__MigrateOnStartup", "true")
    .WithEnvironment("Lab__Profile", profile)
    .WithEnvironment("OpenApi__Enabled", "true")
    .WithEnvironment("Prometheus__OtlpEndpoint", "http://localhost:58390/api/v1/otlp/v1/metrics")
    .WithHttpEndpoint(port: 58380, name: "api", isProxied: false)
    .WaitFor(toxiproxy)
    .WaitFor(prometheus);

await builder.Build().RunAsync();
