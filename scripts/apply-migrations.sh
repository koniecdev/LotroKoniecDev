#!/bin/sh
# Production migration entrypoint (ADR-0008 §6, ticket M6-10).
#
# Runs the two self-contained EF migration bundles baked into Dockerfile.migrator.prod, in order
# (Translation, then Auth — same order as the dev migrator), each against its own connection string
# from the environment. set -e makes ANY failure abort the whole job with a non-zero exit, so the
# pre-deploy job fails and the APIs (which depend on its success) never start against a half-migrated
# schema. Re-running is safe: EF applies only migrations missing from each context's
# __EFMigrationsHistory table.
#
# The connection is passed as `-- --connection <cs>` (forwarded to the design-time factory), NOT via
# the bundle's own `--connection` option: each context's IDesignTimeDbContextFactory builds the
# DbContext first and, without that arg, falls back to reading appsettings.json (absent here). The
# factory's GetArgumentValue("--connection") picks the string up and wires it into the options.
#
# Required environment:
#   ConnectionStrings__TranslationDatabase  - Npgsql connection string for the TMS write context
#   ConnectionStrings__AuthDatabase         - Npgsql connection string for the Auth context
set -e

if [ -z "$ConnectionStrings__TranslationDatabase" ]; then
    echo "FATAL: ConnectionStrings__TranslationDatabase is not set." >&2
    exit 1
fi

if [ -z "$ConnectionStrings__AuthDatabase" ]; then
    echo "FATAL: ConnectionStrings__AuthDatabase is not set." >&2
    exit 1
fi

echo "== MIGRATOR START =="

echo "== Applying Translation migrations =="
/app/tms-migrate -- --connection "$ConnectionStrings__TranslationDatabase"
echo "== TRANSLATION MIGRATOR DONE =="

echo "== Applying Auth migrations =="
/app/auth-migrate -- --connection "$ConnectionStrings__AuthDatabase"
echo "== AUTH MIGRATOR DONE =="

echo "== MIGRATOR COMPLETE =="
