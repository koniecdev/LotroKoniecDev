FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:7644f992230d35cf230017189d4038c0ae0f7388b13f4f7ae1900a155bafb597 AS base
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:ea8bde36c11b6e7eec2656d0e59101d4462f6bd630730f2c8201ed0572b295d5 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY ["Directory.Build.props", "./"]
COPY ["Directory.Packages.props", "./"]
COPY [".editorconfig", "./"]
COPY ["src/Frontend/LotroKoniecDev.Frontend/LotroKoniecDev.Frontend.csproj", "src/Frontend/LotroKoniecDev.Frontend/"]
COPY ["src/AuthSystem/LotroKoniecDev.AuthSystem.Contracts/LotroKoniecDev.AuthSystem.Contracts.csproj", "src/AuthSystem/LotroKoniecDev.AuthSystem.Contracts/"]
COPY ["src/TranslationSystem/LotroKoniecDev.TranslationSystem.Contracts/LotroKoniecDev.TranslationSystem.Contracts.csproj", "src/TranslationSystem/LotroKoniecDev.TranslationSystem.Contracts/"]
COPY ["src/TranslationSystem/LotroKoniecDev.TranslationSystem.Primitives/LotroKoniecDev.TranslationSystem.Primitives.csproj", "src/TranslationSystem/LotroKoniecDev.TranslationSystem.Primitives/"]
COPY ["src/SharedKernel/LotroKoniecDev.SharedKernel/LotroKoniecDev.SharedKernel.csproj", "src/SharedKernel/LotroKoniecDev.SharedKernel/"]
COPY ["src/Utilities/LotroKoniecDev.Hateoas.Abstractions/LotroKoniecDev.Hateoas.Abstractions.csproj", "src/Utilities/LotroKoniecDev.Hateoas.Abstractions/"]
COPY ["src/Utilities/LotroKoniecDev.Logging/LotroKoniecDev.Logging.csproj", "src/Utilities/LotroKoniecDev.Logging/"]
COPY ["src/Utilities/LotroKoniecDev.Options/LotroKoniecDev.Options.csproj", "src/Utilities/LotroKoniecDev.Options/"]

RUN dotnet restore "src/Frontend/LotroKoniecDev.Frontend/LotroKoniecDev.Frontend.csproj"
ARG CACHEBUST=0
COPY . .
WORKDIR "/src/src/Frontend/LotroKoniecDev.Frontend"

# No `--no-restore` here, unlike the API images (ADR-0028, amended). `blazor.web.js` lives in the
# implicit `Microsoft.AspNetCore.App.Internal.Assets` package, which the SDK references only once it
# sees `.razor` files — that is, after `COPY . .`, never during the `.csproj`-only restore above. With
# `--no-restore` the build cannot fetch it and still exits 0, emitting a static-web-assets manifest
# with no `_framework/*`; `MapStaticAssets()` then 404s every asset at runtime. `publish` below keeps
# `--no-restore`: by then this build has restored the full graph.
RUN dotnet build "./LotroKoniecDev.Frontend.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./LotroKoniecDev.Frontend.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false --no-restore

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

# Data Protection keyring mount point (ADR-0005). A non-dev deployment MUST mount a shared/persistent
# volume here and set DataProtection__KeyRingPath=/keys, or the startup guard fails fast. A named
# volume covers single-host restart/scale; multi-host needs ReadWriteMany (NFS/EFS/Azure Files). No
# VOLUME instruction on purpose: an anonymous volume would silently mask a missing mount. The dev
# compose runs under Development, where an empty keyring path is valid (host-default location), so
# this directory is unused there.
USER root
RUN mkdir -p /keys && chown app:app /keys
USER $APP_UID

# Probe the same liveness endpoint the cloud probes hit (iac/azure-container-apps.tf), not `/`:
# the root path runs the whole OIDC + antiforgery pipeline, so it reports on far more than "this
# process is alive" and a redirect there passes `curl -f` without proving anything.
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD curl -f http://localhost:8080/health/live || exit 1

ENTRYPOINT ["dotnet", "LotroKoniecDev.Frontend.dll"]
