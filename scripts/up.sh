#!/usr/bin/env bash
# Boots the INFRA-ONLY dev stack (ADR-0006, amended by #190 / M6-14): postgres + migrator + mailpit +
# aspire-dashboard. The migrator runs once and exits; no app images are built here.
# The three apps run on the HOST, not in compose:
#   auth-api  https://localhost:5003   (dotnet run --project src/AuthSystem/LotroKoniecDev.AuthSystem.API)
#   tms-api   https://localhost:5002   (dotnet run --project src/TranslationSystem/LotroKoniecDev.TranslationSystem.API)
#   frontend  https://localhost:7017   (dotnet run --project src/Frontend/LotroKoniecDev.Frontend)
# or all three at once via the Rider compound ".run/TMS dev (all hosts).run.xml".
#
# compose has no secret defaults, so a .env is required; this creates it from .env.example on first
# run (edit secrets there afterwards), then `docker compose up`. Extra args pass through.
#
# One-time host prerequisite (the host Kestrels serve HTTPS with the native ASP.NET Core dev cert):
#   dotnet dev-certs https --trust

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
ENV_FILE="$REPO_ROOT/.env"

if [ ! -f "$ENV_FILE" ]; then
    cp "$REPO_ROOT/.env.example" "$ENV_FILE"
    echo ".env created from .env.example. Edit secrets if needed."
fi

cd "$REPO_ROOT"
exec docker compose up "$@"
