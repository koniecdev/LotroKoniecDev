#!/usr/bin/env bash
# N-1 backward-compatibility proof (MIGR-05 / ADR-0024) — the executable counterpart of the
# check-migration-safety.sh text gate.
#
# ADR-0023: every migration must be N-1 backward-compatible — during each deploy's smoke window
# (and after any failed rollout) the PREVIOUS app revision serves on the NEW schema. This script
# executes that exact combination: it migrates throwaway PostgreSQL containers to the schema of
# the CURRENT tree and runs the PREVIOUS release's own integration suites against them.
#
# Mechanics:
#   1. Generate idempotent schema scripts from the current tree (dotnet ef migrations script
#      --idempotent) for both contexts — ApplicationWriteDbContext and AuthDbContext — using the
#      same project/startup pairs as Dockerfile.migrator.
#   2. Check out the previous release (default: HEAD^1 — on a PR merge commit that is the main
#      tip being merged into, i.e. the deployed release; the repo has no version tags and CD
#      ships every main commit) into a temporary git worktree.
#   3. Run every *.Tests.Integration.csproj the OLD checkout discovers (the same convention
#      pr-verify.yml gates on — no silent narrowing) with N1_COMPAT_SCHEMA_SCRIPTS_DIR pointing
#      at the generated scripts. Each old test fixture applies the HEAD schema to its container
#      before its own MigrateAsync (which then no-ops). Red = the old release cannot live on the
#      new schema = a backward-incompatible migration.
#
# Bootstrap: if the previous release predates the seam (no N1_COMPAT_SCHEMA_SCRIPTS_DIR marker in
# its tests/), there is nothing it can prove — report loudly and pass. The window closes at the
# first post-seam merge. The seam missing from the CURRENT tree is a hard error instead: that
# would silence the proof for every future run.
#
# Local usage:
#   ./scripts/n1-compat.sh                  # previous release := HEAD^1
#   ./scripts/n1-compat.sh origin/main      # previous release := main tip
#   ./scripts/n1-compat.sh HEAD             # self-check; with an uncommitted destructive
#                                           # migration in the tree this MUST go red (the
#                                           # deliberate-red experiment from #340's AC)
#
# Requires: Docker (Testcontainers), the pinned dotnet-ef tool (dotnet tool restore runs here).
# Exit codes: 0 = compatible (or bootstrap), 1 = N-1 incompatibility (old suite red), 2 = cannot run.
set -euo pipefail

CODE_REF="${1:-HEAD^1}"
SEAM_MARKER='N1_COMPAT_SCHEMA_SCRIPTS_DIR'
SEAM_CALL='N1CompatSchemaSeam.ApplyIfConfiguredAsync'
SEAM_FILES=(
  "tests/LotroKoniecDev.TranslationSystem.API.Tests.Integration/N1CompatSchemaSeam.cs"
  "tests/LotroKoniecDev.AuthSystem.API.Tests.Integration/N1CompatSchemaSeam.cs"
)
# The factories must keep CALLING the seam — a removed call site (seam file intact) would make
# every future run silently vacuous: the old suite would migrate its own old schema and pass.
SEAM_CALL_SITES=(
  "tests/LotroKoniecDev.TranslationSystem.API.Tests.Integration/TranslationSystemApiFactory.cs"
  "tests/LotroKoniecDev.AuthSystem.API.Tests.Integration/AuthSystemApiFactory.cs"
)
# Never used to connect: script generation is offline, but the design-time factories require a
# syntactically valid connection string (same mechanism as Dockerfile.migrator / apply-migrations.sh).
DESIGN_TIME_CONNECTION='Host=localhost;Database=design_time_only;Username=postgres;Password=unused'

repo_root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$repo_root"

say() {
  echo "$1"
  if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
    echo "$1" >> "$GITHUB_STEP_SUMMARY"
  fi
}

schema_dir=""
worktree_parent=""
worktree_dir=""
cleanup() {
  if [ -n "$worktree_dir" ] && [ -d "$worktree_dir" ]; then
    git worktree remove --force "$worktree_dir" >/dev/null 2>&1 || true
    git worktree prune >/dev/null 2>&1 || true
  fi
  if [ -n "$worktree_parent" ] && [ -d "$worktree_parent" ]; then
    rm -rf "$worktree_parent"
  fi
  if [ -n "$schema_dir" ] && [ -d "$schema_dir" ]; then
    rm -rf "$schema_dir"
  fi
}
trap cleanup EXIT

# mktemp output must be canonicalized (pwd -P): on macOS it lives under /var → /private/var, and
# feeding the symlinked form to dotnet as an absolute project path makes restore and build
# disagree about project identity — the compile then silently resolves ZERO references
# (empirically: 646 CS errors via /var/..., a clean build via /private/var/...).
canonical_tmp_dir() {
  local dir
  dir="$(mktemp -d)"
  (cd "$dir" && pwd -P)
}

# --- 0. The seam must exist AND be called in the CURRENT tree — losing either silences the ----
# --- proof forever (the old suite would just migrate its own old schema and pass). ------------
for seam_file in "${SEAM_FILES[@]}"; do
  if ! grep -q "$SEAM_MARKER" "$seam_file" 2>/dev/null; then
    echo "ERROR: '$seam_file' no longer carries the $SEAM_MARKER seam." >&2
    echo "The N-1 job cannot prove anything without it (ADR-0024 §3). Restore the seam or update this script." >&2
    exit 2
  fi
done

for factory_file in "${SEAM_CALL_SITES[@]}"; do
  if ! grep -qF "$SEAM_CALL" "$factory_file" 2>/dev/null; then
    echo "ERROR: '$factory_file' no longer calls $SEAM_CALL." >&2
    echo "Without the call the seam is dead code and every N-1 run is vacuous green (ADR-0024 §3). Restore the call or update this script." >&2
    exit 2
  fi
done

prev_sha="$(git rev-parse --verify --quiet "${CODE_REF}^{commit}")" || {
  echo "ERROR: cannot resolve '$CODE_REF' to a commit." >&2
  exit 2
}

# --- 1. Generate the HEAD schema as idempotent scripts, one per context. ----------------------
schema_dir="$(canonical_tmp_dir)"
echo "== Generating idempotent schema scripts from the current tree =="
# `|| exit 2` on the generation phase keeps the exit-code contract honest: an infra failure here
# is "cannot run", never the exit-1 "backward-incompatible migration" verdict.
dotnet tool restore >/dev/null || exit 2

# Explicit restore first: dotnet-ef's project-metadata query (msbuild /t:ResolvePackageAssets)
# does NOT implicit-restore, so on a fresh checkout (CI, clean clone) it dies with NETSDK1004
# "Assets file ... project.assets.json not found". Same two startup projects as Dockerfile.migrator.
dotnet restore src/TranslationSystem/LotroKoniecDev.TranslationSystem.Persistence/LotroKoniecDev.TranslationSystem.Persistence.csproj || exit 2
dotnet restore src/AuthSystem/LotroKoniecDev.AuthSystem.API/LotroKoniecDev.AuthSystem.API.csproj || exit 2

# No --startup-project here: it equals --project, and dotnet-ef 10.0.9's parser mis-reads the
# pair when both carry the identical value ("Unable to retrieve project metadata"); omitting it
# makes the startup project default to --project, which is exactly what we want.
dotnet ef migrations script --idempotent --output "$schema_dir/translation.sql" \
  --project src/TranslationSystem/LotroKoniecDev.TranslationSystem.Persistence \
  --context ApplicationWriteDbContext \
  -- --connection "$DESIGN_TIME_CONNECTION" || exit 2

dotnet ef migrations script --idempotent --output "$schema_dir/auth.sql" \
  --project src/AuthSystem/LotroKoniecDev.AuthSystem.Persistence \
  --startup-project src/AuthSystem/LotroKoniecDev.AuthSystem.API \
  --context AuthDbContext \
  -- --connection "$DESIGN_TIME_CONNECTION" || exit 2

for script in translation auth; do
  migration_count="$(grep -c "INSERT INTO.*\"__EFMigrationsHistory\"" "$schema_dir/$script.sql" || true)"
  if [ "$migration_count" -eq 0 ]; then
    echo "ERROR: '$script.sql' contains no migration-history inserts — generation produced an unusable script." >&2
    exit 2
  fi
  echo "   $script.sql: $migration_count migration(s)"
done

# --- 2. Check out the previous release into a worktree. ---------------------------------------
worktree_parent="$(canonical_tmp_dir)"
worktree_dir="$worktree_parent/n1-prev"
echo "== Previous release: $prev_sha ($(git log -1 --format=%s "$prev_sha")) =="
git worktree add --detach --quiet "$worktree_dir" "$prev_sha" || exit 2

if ! grep -rq "$SEAM_MARKER" "$worktree_dir/tests" --include="*.cs" 2>/dev/null; then
  say "N-1 compat: BOOTSTRAP — previous release $prev_sha predates the $SEAM_MARKER seam (ADR-0024); nothing it can prove yet. The window closes at the first post-seam merge."
  exit 0
fi

# --- 3. Run the OLD release's integration suites against the NEW schema. ----------------------
status=0
suites=0
while IFS= read -r proj; do
  suites=$((suites + 1))
  echo "::group::$(basename "$proj") (previous release, HEAD schema)"
  if ! (cd "$worktree_dir" && \
        N1_COMPAT_SCHEMA_SCRIPTS_DIR="$schema_dir" \
        dotnet test "$proj" --configuration Release --logger "console;verbosity=minimal"); then
    status=1
  fi
  echo "::endgroup::"
done < <(find "$worktree_dir/tests" -name '*.Tests.Integration.csproj' | sort)

if [ "$suites" -eq 0 ]; then
  echo "ERROR: no *.Tests.Integration.csproj found in the previous release — refusing to report a false green." >&2
  exit 2
fi

if [ "$status" -ne 0 ]; then
  say "N-1 compat: RED — the previous release ($prev_sha) fails on the current schema. A migration in this change is backward-incompatible (ADR-0023): ship it as expand → backfill → contract, or fix the breaking DDL. (If the old suite failed to build or start rather than failing tests, it's an infra problem — check the log above.)"
  exit 1
fi

say "N-1 compat: GREEN — previous release $prev_sha passes all $suites integration suite(s) on the current schema."
