#!/usr/bin/env bash
#
# Offline self-test for scripts/ci/classify-changes.sh — the classifier that decides whether a PR
# runs the .NET gate, the bash guards, the image build, or nothing at all.
#
# It runs UNCONDITIONALLY in the `changes` job, before the classifier is trusted to classify
# anything: a classifier that mis-classifies real source as inert would silently skip the build and
# the tests, and no other check would notice. The false-green half of the table below is therefore
# the point of this file — the "skips the right things" half only guards the CI bill.
#
# Pure bash + git-free: every case feeds a path list on stdin through the --files seam.
set -uo pipefail

SCRIPTS_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
CLASSIFY="$SCRIPTS_DIR/ci/classify-changes.sh"

failures=0
cases=0

# expect <expected-verdicts> <description> <path>...
expect() {
    local expected="$1" description="$2"
    shift 2
    local actual
    actual="$(printf '%s\n' "$@" | "$CLASSIFY" --files 2>/dev/null | tr '\n' ' ')"
    actual="${actual% }"
    cases=$((cases + 1))
    if [ "$actual" = "$expected" ]; then
        printf 'PASS  %s\n' "$description"
    else
        printf 'FAIL  %s\n        expected: %s\n        actual:   %s\n' "$description" "$expected" "$actual"
        failures=$((failures + 1))
    fi
}

# expect_code <description> <path>  — the false-green half: this file MUST face the .NET gate.
expect_code() {
    local description="$1" path="$2"
    local actual
    actual="$(printf '%s\n' "$path" | "$CLASSIFY" --files 2>/dev/null | grep '^code=')"
    cases=$((cases + 1))
    if [ "$actual" = 'code=true' ]; then
        printf 'PASS  %s\n' "$description"
    else
        printf 'FAIL  %s\n        expected: code=true\n        actual:   %s\n' "$description" "$actual"
        failures=$((failures + 1))
    fi
}

echo '── the .NET gate is never skipped for a real build input ─────────────────────────────────────'
expect_code 'a C# source file'          'src/TranslationSystem/LotroKoniecDev.TranslationSystem.API/Features/Translations/Approve.cs'
expect_code 'a Razor component'         'src/Frontend/LotroKoniecDev.Frontend/Components/Pages/Editor.razor'
expect_code 'a project file'            'src/Patcher/LotroKoniecDev.Cli/LotroKoniecDev.Cli.csproj'
expect_code 'the solution file'         'LotroKoniecDev.slnx'
expect_code 'central package versions'  'Directory.Packages.props'
expect_code 'repo-wide build props'     'Directory.Build.props'
expect_code 'the SDK pin'               'global.json'
expect_code 'the NuGet config'          'nuget.config'
expect_code 'the analyzer config'       '.editorconfig'
expect_code 'a test source file'        'tests/LotroKoniecDev.Tests.Unit/FragmentTests.cs'
expect_code 'a golden test fixture'     'tests/LotroKoniecDev.Tests.Unit/Fixtures/exported.txt'
expect_code 'an EF migration'           'src/TranslationSystem/LotroKoniecDev.TranslationSystem.Persistence/Migrations/20260713_Add.cs'
expect_code 'appsettings'               'src/TranslationSystem/LotroKoniecDev.TranslationSystem.API/appsettings.json'
expect_code 'an unrecognized new path'  'tools/whatever/Program.cs'

echo
echo '── inert: nothing runs ───────────────────────────────────────────────────────────────────────'
expect 'code=false guards=false images=false' 'agent config under .claude/'   '.claude/agents/code-reviewer.md' '.claude/settings.json'
expect 'code=false guards=false images=false' 'project memory + docs'         'CLAUDE.md' 'docs/claude-loop.md'
expect 'code=false guards=false images=false' 'the loop conductor script'     'scripts/claude/backlog-loop.sh'
expect 'code=false guards=false images=false' 'another workflow'              '.github/workflows/codeql.yml'
expect 'code=false guards=false images=false' 'the dev compose stack'         'compose.yaml'
expect 'code=false guards=false images=false' 'an env example'                '.env.example'

echo
echo '── guards only: the bash gates re-run, the .NET gate does not ────────────────────────────────'
expect 'code=false guards=true images=false' 'a script the provenance self-test executes' 'scripts/claude/work-ticket.sh'
expect 'code=false guards=true images=false' 'the provenance gate itself'                 'scripts/claude/issue-trust.sh'
expect 'code=false guards=true images=false' 'the SSR-purity guard'                       'scripts/check-ssr-purity.sh'
expect 'code=false guards=true images=false' 'the migration-safety guard'                 'scripts/check-migration-safety.sh'
expect 'code=false guards=true images=false' 'a guard self-test'                          'scripts/tests/check-migration-safety.tests.sh'
expect 'code=false guards=true images=false' 'the script CD runs on a prod box'           'scripts/hetzner/deploy.sh'
expect 'code=false guards=true images=false' 'the rollout self-test'                      'scripts/tests/hetzner-deploy.tests.sh'
expect 'code=false guards=true images=false' 'the prod N-1 promotion gate resolver'       'scripts/ci/resolve-prod-baseline.sh'
expect 'code=false guards=true images=false' 'the resolver self-test'                     'scripts/tests/resolve-prod-baseline.tests.sh'
expect 'code=false guards=true images=false' 'the promotion gate verdict mapping'         'scripts/ci/n1-promotion-gate.sh'
expect 'code=false guards=true images=false' 'the verdict-mapping self-test'              'scripts/tests/n1-promotion-gate.tests.sh'

echo
echo '── images ───────────────────────────────────────────────────────────────────────────────────'
expect 'code=false guards=true images=true'  'a shipped Dockerfile (restore-graph guard reads it)' 'src/Frontend/LotroKoniecDev.Frontend/Dockerfile'
expect 'code=false guards=true images=true'  'the migrator Dockerfile'                             'Dockerfile.migrator.prod'
expect 'code=false guards=false images=true' 'the build-context filter'                            '.dockerignore'

echo
echo '── the gate definitions validate themselves ─────────────────────────────────────────────────'
expect 'code=true guards=true images=true'  'pr-verify defines all three jobs' '.github/workflows/pr-verify.yml'
expect 'code=true guards=true images=false' 'ci defines the .NET gate on main' '.github/workflows/ci.yml'

echo
echo '── fail open ────────────────────────────────────────────────────────────────────────────────'
expect 'code=true guards=true images=true'  'an empty diff runs everything' ''
expect 'code=true guards=true images=false' 'docs + source together still build' 'docs/adr/0035.md' 'src/Patcher/LotroKoniecDev.Domain/Result.cs'

echo
if [ "$failures" -gt 0 ]; then
    printf '%d/%d cases FAILED\n' "$failures" "$cases"
    exit 1
fi
printf 'all %d cases passed\n' "$cases"
