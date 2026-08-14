FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:207cc51496778557731c81ff670333d8ade4a4fec22768fd1be8e78474a84ecf AS base
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:e1fc6e423f543119c406d24e2e687d67c569f18f04a37a8b0005d80ad0dcee80 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY ["Directory.Build.props", "./"]
COPY ["Directory.Packages.props", "./"]
COPY [".editorconfig", "./"]
COPY ["src/TranslationSystem/LotroKoniecDev.TranslationSystem.API/LotroKoniecDev.TranslationSystem.API.csproj", "src/TranslationSystem/LotroKoniecDev.TranslationSystem.API/"]
COPY ["src/TranslationSystem/LotroKoniecDev.TranslationSystem.Contracts/LotroKoniecDev.TranslationSystem.Contracts.csproj", "src/TranslationSystem/LotroKoniecDev.TranslationSystem.Contracts/"]
COPY ["src/TranslationSystem/LotroKoniecDev.TranslationSystem.Persistence/LotroKoniecDev.TranslationSystem.Persistence.csproj", "src/TranslationSystem/LotroKoniecDev.TranslationSystem.Persistence/"]
COPY ["src/TranslationSystem/LotroKoniecDev.TranslationSystem.Domain/LotroKoniecDev.TranslationSystem.Domain.csproj", "src/TranslationSystem/LotroKoniecDev.TranslationSystem.Domain/"]
COPY ["src/TranslationSystem/LotroKoniecDev.TranslationSystem.Primitives/LotroKoniecDev.TranslationSystem.Primitives.csproj", "src/TranslationSystem/LotroKoniecDev.TranslationSystem.Primitives/"]
COPY ["src/TranslationSystem/LotroKoniecDev.TranslationSystem.ReadModels/LotroKoniecDev.TranslationSystem.ReadModels.csproj", "src/TranslationSystem/LotroKoniecDev.TranslationSystem.ReadModels/"]
COPY ["src/TranslationSystem/LotroKoniecDev.TranslationSystem.ReadModels.EntityFramework/LotroKoniecDev.TranslationSystem.ReadModels.EntityFramework.csproj", "src/TranslationSystem/LotroKoniecDev.TranslationSystem.ReadModels.EntityFramework/"]
COPY ["src/TranslationSystem/LotroKoniecDev.TranslationSystem.Projections/LotroKoniecDev.TranslationSystem.Projections.csproj", "src/TranslationSystem/LotroKoniecDev.TranslationSystem.Projections/"]
COPY ["src/SharedKernel/LotroKoniecDev.SharedKernel/LotroKoniecDev.SharedKernel.csproj", "src/SharedKernel/LotroKoniecDev.SharedKernel/"]
COPY ["src/Utilities/LotroKoniecDev.Hateoas/LotroKoniecDev.Hateoas.csproj", "src/Utilities/LotroKoniecDev.Hateoas/"]
COPY ["src/Utilities/LotroKoniecDev.Hateoas.Abstractions/LotroKoniecDev.Hateoas.Abstractions.csproj", "src/Utilities/LotroKoniecDev.Hateoas.Abstractions/"]
COPY ["src/Utilities/LotroKoniecDev.Logging/LotroKoniecDev.Logging.csproj", "src/Utilities/LotroKoniecDev.Logging/"]
COPY ["src/Utilities/LotroKoniecDev.Options/LotroKoniecDev.Options.csproj", "src/Utilities/LotroKoniecDev.Options/"]

RUN dotnet restore "src/TranslationSystem/LotroKoniecDev.TranslationSystem.API/LotroKoniecDev.TranslationSystem.API.csproj"
ARG CACHEBUST=0
COPY . .
WORKDIR "/src/src/TranslationSystem/LotroKoniecDev.TranslationSystem.API"
RUN dotnet build "./LotroKoniecDev.TranslationSystem.API.csproj" -c $BUILD_CONFIGURATION -o /app/build --no-restore

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./LotroKoniecDev.TranslationSystem.API.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false --no-restore

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD curl -f http://localhost:8080/health/live || exit 1

ENTRYPOINT ["dotnet", "LotroKoniecDev.TranslationSystem.API.dll"]
