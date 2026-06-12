FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY ["Directory.Build.props", "./"]
COPY ["Directory.Packages.props", "./"]
COPY [".editorconfig", "./"]
COPY ["src/AuthSystem/LotroKoniecDev.AuthSystem.API/LotroKoniecDev.AuthSystem.API.csproj", "src/AuthSystem/LotroKoniecDev.AuthSystem.API/"]
COPY ["src/AuthSystem/LotroKoniecDev.AuthSystem.Domain/LotroKoniecDev.AuthSystem.Domain.csproj", "src/AuthSystem/LotroKoniecDev.AuthSystem.Domain/"]
COPY ["src/AuthSystem/LotroKoniecDev.AuthSystem.Contracts/LotroKoniecDev.AuthSystem.Contracts.csproj", "src/AuthSystem/LotroKoniecDev.AuthSystem.Contracts/"]
COPY ["src/AuthSystem/LotroKoniecDev.AuthSystem.Persistence/LotroKoniecDev.AuthSystem.Persistence.csproj", "src/AuthSystem/LotroKoniecDev.AuthSystem.Persistence/"]
COPY ["src/SharedKernel/LotroKoniecDev.SharedKernel/LotroKoniecDev.SharedKernel.csproj", "src/SharedKernel/LotroKoniecDev.SharedKernel/"]
COPY ["src/AuthSystem/LotroKoniecDev.AuthSystem.Infrastructure/LotroKoniecDev.AuthSystem.Infrastructure.csproj", "src/AuthSystem/LotroKoniecDev.AuthSystem.Infrastructure/"]
COPY ["src/Utilities/LotroKoniecDev.Logging/LotroKoniecDev.Logging.csproj", "src/Utilities/LotroKoniecDev.Logging/"]
COPY ["src/Utilities/LotroKoniecDev.Options/LotroKoniecDev.Options.csproj", "src/Utilities/LotroKoniecDev.Options/"]

RUN dotnet restore "src/AuthSystem/LotroKoniecDev.AuthSystem.API/LotroKoniecDev.AuthSystem.API.csproj"
ARG CACHEBUST=0
COPY . .
WORKDIR "/src/src/AuthSystem/LotroKoniecDev.AuthSystem.API"
RUN dotnet build "./LotroKoniecDev.AuthSystem.API.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./LotroKoniecDev.AuthSystem.API.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

USER app

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD curl -f http://localhost:8080/health/live || exit 1

ENTRYPOINT ["dotnet", "LotroKoniecDev.AuthSystem.API.dll"]
