# syntax=docker/dockerfile:1
# Two images from one build: `api` (the ledger service under test) and `chaos` (the experiment runner used by the
# compose demo and the CI smoke test). Both run on the chiseled ASP.NET runtime: no shell, non-root.

# ---- build -------------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore in its own layer: it only re-runs when package versions or project files change.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/SloChaosLab.Ledger/SloChaosLab.Ledger.csproj src/SloChaosLab.Ledger/
COPY src/SloChaosLab.Slo/SloChaosLab.Slo.csproj src/SloChaosLab.Slo/
COPY src/SloChaosLab.Api/SloChaosLab.Api.csproj src/SloChaosLab.Api/
COPY tools/SloChaosLab.Chaos/SloChaosLab.Chaos.csproj tools/SloChaosLab.Chaos/
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet restore src/SloChaosLab.Api/SloChaosLab.Api.csproj && \
    dotnet restore tools/SloChaosLab.Chaos/SloChaosLab.Chaos.csproj

COPY .editorconfig ./
COPY src/ src/
COPY tools/SloChaosLab.Chaos/ tools/SloChaosLab.Chaos/
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet publish src/SloChaosLab.Api/SloChaosLab.Api.csproj \
        --configuration Release --no-restore --output /app/api -p:UseAppHost=false && \
    dotnet publish tools/SloChaosLab.Chaos/SloChaosLab.Chaos.csproj \
        --configuration Release --no-restore --output /app/chaos -p:UseAppHost=false

# ---- chaos runner ------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS chaos
LABEL org.opencontainers.image.title="slo-chaos-lab-chaos" \
      org.opencontainers.image.source="https://github.com/hekimcan206/slo-chaos-lab" \
      org.opencontainers.image.licenses="MIT"
WORKDIR /app
COPY --from=build --link /app/chaos .
USER $APP_UID
ENTRYPOINT ["dotnet", "chaos.dll"]

# ---- api (default target) ----------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS api
LABEL org.opencontainers.image.title="slo-chaos-lab-api" \
      org.opencontainers.image.source="https://github.com/hekimcan206/slo-chaos-lab" \
      org.opencontainers.image.licenses="MIT"
WORKDIR /app
COPY --from=build --link /app/api .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "SloChaosLab.Api.dll"]
