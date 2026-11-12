# Contributing

Thanks for taking the time to improve slo-chaos-lab.

## Prerequisites

- .NET SDK 10.0.400 or later (`global.json` rolls forward to the latest feature band)
- Docker (integration tests start PostgreSQL and Toxiproxy through Testcontainers; `docker compose up` runs the lab)
- Node 22 only to regenerate the Grafana dashboard (`node tools/dashboard/build-dashboard.mjs`)

## Workflow

1. Open an issue first for anything larger than a small fix, so we can agree on the approach.
2. Branch from `main` and keep each pull request to one concern.
3. Before pushing:

   ```bash
   dotnet build                 # must be warning-free: warnings are errors
   dotnet test                  # unit + integration tests (Docker running)
   dotnet format --verify-no-changes
   docker run --rm -v "$PWD/deploy/prometheus:/p" -w /p/tests --entrypoint promtool \
     prom/prometheus:v3.15.1 test rules slo-rules.test.yml
   ```

## Changing an SLO

SLOs are code. Edit `src/SloChaosLab.Slo/SloCatalog.cs` (or `BurnRateAlertPolicy.cs`), then regenerate the rules:

```bash
dotnet run --project tools/SloChaosLab.Chaos -- generate-rules
```

The golden test fails until the committed `deploy/prometheus/rules/slo-rules.yml` matches, and the promtool tests in
`deploy/prometheus/tests` must keep passing (update their expectations only with a reason in the PR).

## Adding an experiment

Add it to `tools/SloChaosLab.Chaos/Experiments.cs`, run it before and after (`sh scripts/run-experiments.sh <name>`),
commit the JSON results and screenshot, and write a postmortem in `docs/postmortems/` using an existing one as the
template. Numbers in docs come from those JSON files only.

## Commit messages

[Conventional Commits](https://www.conventionalcommits.org/) are required (`feat:`, `fix:`, `docs:`, `test:`,
`build:`, `ci:`, `refactor:`, `perf:`, `chore:`). They keep the history readable and the hand-written CHANGELOG easy
to assemble.

## Architecture decisions

A change to a decision recorded in `docs/adr/` needs a new ADR that supersedes the old one (MADR format: context,
options considered, decision, consequences). Do not edit an accepted ADR in place.

## Database migrations

Migrations are numbered SQL files in `src/SloChaosLab.Api/Persistence/Migrations`. An applied migration must never
change (the runner checks a SHA-256); add a new file instead.
