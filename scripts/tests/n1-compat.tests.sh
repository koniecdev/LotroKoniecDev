#!/usr/bin/env bash
# Test suite for the N-1 proof's verdict classification (scripts/n1-compat.sh — ADR-0024, #679).
#
# The property here is WHICH failure earns WHICH exit code. ADR-0024's deploy-time amendment gives
# the two opposite meanings: exit 1 says the serving release cannot live on this schema (do NOT
# retry — promote in smaller steps), exit 2 says the proof never ran (the batch is UNJUDGED and the
# fix is the infra). Only a suite that actually BUILT and then failed its tests can earn exit 1; a
# previous release that no longer restores or compiles has proven nothing at all.
#
# CD #236 is why this suite exists. The serving release pinned Testcontainers 4.13.0 -> SSH.NET
# 2025.1.0, an advisory landed on it after that release shipped, its restore died on NU1903
# "Warning As Error", and the collapsed mapping reported "your migrations break prod" — telling the
# approver to split a batch whose migrations were never judged.
#
# Every case runs the REAL scripts/n1-compat.sh over a fixture git repo with `dotnet` STUBBED on
# PATH, so the whole control flow is exercised without Docker, .NET, a schema or a container. git,
# find and grep stay real — the worktree and the discovery loop are part of what is under test.
#
# CI runs this in the `guards` job, next to the other bash gates.

set -euo pipefail

SCRIPT_SH="$(cd "$(dirname "$0")/.." && pwd)/n1-compat.sh"
TMP_ROOT="$(cd "$(mktemp -d)" && pwd -P)"
trap 'rm -rf "$TMP_ROOT"' EXIT

cases=0
CASE=""
LAST_OUTPUT=""
LAST_STATUS=0
STUB_LOG="$TMP_ROOT/dotnet.log"

fail() {
    printf '✗ [%s] %s\n' "$CASE" "$1"
    if [ -n "${2:-}" ]; then
        printf '%s\n' "$2" | sed 's/^/    /'
    fi
    printf '  --- n1-compat.sh output ---\n%s\n' "$LAST_OUTPUT" | sed 's/^/    /'
    printf '  --- dotnet invocations ---\n%s\n' "$(cat "$STUB_LOG" 2>/dev/null)" | sed 's/^/    /'
    exit 1
}

pass() {
    cases=$((cases + 1))
    printf '✓ [%s] %s\n' "$CASE" "$1"
}

# --- Fixture: a real git repo carrying the seam, two integration suites and the real script -----
FIXTURE="$TMP_ROOT/repo"
SUITES=(
    'LotroKoniecDev.AuthSystem.API.Tests.Integration:AuthSystemApiFactory'
    'LotroKoniecDev.TranslationSystem.API.Tests.Integration:TranslationSystemApiFactory'
)

mkdir -p "$FIXTURE/scripts"
cp "$SCRIPT_SH" "$FIXTURE/scripts/n1-compat.sh"
for entry in "${SUITES[@]}"; do
    suite="${entry%%:*}"
    factory="${entry##*:}"
    mkdir -p "$FIXTURE/tests/$suite"
    # The seam marker and its call site are what the script asserts before it will prove anything.
    echo 'var dir = Environment.GetEnvironmentVariable("N1_COMPAT_SCHEMA_SCRIPTS_DIR");' \
        > "$FIXTURE/tests/$suite/N1CompatSchemaSeam.cs"
    echo 'await N1CompatSchemaSeam.ApplyIfConfiguredAsync(container);' \
        > "$FIXTURE/tests/$suite/$factory.cs"
    echo '<Project Sdk="Microsoft.NET.Sdk" />' > "$FIXTURE/tests/$suite/$suite.csproj"
done

git -C "$FIXTURE" init --quiet --initial-branch=main
git -C "$FIXTURE" config user.email 'n1-compat-tests@example.invalid'
git -C "$FIXTURE" config user.name 'n1-compat tests'
git -C "$FIXTURE" add -A
git -C "$FIXTURE" commit --quiet -m 'fixture: previous release'

# --- The `dotnet` stub -------------------------------------------------------------------------
# It logs every invocation (so a case can prove what did and did NOT run) and fails the phase named
# by STUB_<PHASE>_FAIL_MATCH for the project whose path contains that substring. Restore is asked
# to tell the two trees apart by cwd: the HEAD-tree restores run in the fixture root, the previous
# release's run inside the worktree.
STUB_BIN="$TMP_ROOT/bin"
mkdir -p "$STUB_BIN"
cat > "$STUB_BIN/dotnet" <<'STUB'
#!/usr/bin/env bash
# The seam dir is logged as its own field: it is the single line that makes the whole proof
# non-vacuous (ADR-0024 §3), so the suite has to be able to assert it reached the run.
printf '%s|%s|%s\n' "$PWD" "${N1_COMPAT_SCHEMA_SCRIPTS_DIR:-}" "$*" >> "$N1_STUB_LOG"

phase="$1"
shift
STUB_ARGV="$*"

matches() {
    local needle="$1"
    shift
    [ -n "$needle" ] && printf '%s\n' "$*" | grep -qF -- "$needle"
}

case "$phase" in
    tool)
        exit 0
        ;;
    restore)
        # The current tree's own restores are not what this proof judges — always fine.
        [ "$PWD" = "$N1_FIXTURE_ROOT" ] && exit 0
        matches "${STUB_RESTORE_FAIL_MATCH:-}" "$@" && exit 1
        exit 0
        ;;
    ef)
        output=''
        while [ $# -gt 0 ]; do
            [ "$1" = '--output' ] && output="$2"
            shift
        done
        [ -n "$output" ] || exit 9
        echo 'INSERT INTO "__EFMigrationsHistory" ("MigrationId") VALUES (N'"'"'20260101_Stub'"'"');' > "$output"
        exit 0
        ;;
    build)
        matches "${STUB_BUILD_FAIL_MATCH:-}" "$@" && exit 1
        exit 0
        ;;
    test)
        # Mirror VSTest: write the trx the caller asked for, and report zero executed tests with
        # exit 0 — "No test is available in ….dll" is a warning, not a failure (VSTest 18.0.1).
        results_dir='.'
        log_name='stub.trx'
        for arg in "$@"; do
            case "$arg" in
                trx\;LogFileName=*) log_name="${arg#*LogFileName=}" ;;
            esac
        done
        while [ $# -gt 0 ]; do
            [ "$1" = '--results-directory' ] && results_dir="$2"
            shift
        done
        executed=2
        failed=0
        matches "${STUB_TEST_NO_TESTS_MATCH:-}" "$STUB_ARGV" && executed=0
        matches "${STUB_TEST_FAIL_MATCH:-}" "$STUB_ARGV" && failed=1
        mkdir -p "$results_dir"
        printf '<Counters total="%s" executed="%s" passed="%s" failed="%s" />\n' \
            "$executed" "$executed" "$((executed - failed))" "$failed" > "$results_dir/$log_name"
        [ "$failed" -ne 0 ] && exit 1
        exit 0
        ;;
esac
exit 0
STUB
chmod +x "$STUB_BIN/dotnet"

run_proof() {
    : > "$STUB_LOG"
    : > "$TMP_ROOT/step_summary"
    LAST_STATUS=0
    LAST_OUTPUT="$( (cd "$FIXTURE" && env \
        PATH="$STUB_BIN:$PATH" \
        GITHUB_STEP_SUMMARY="$TMP_ROOT/step_summary" \
        N1_STUB_LOG="$STUB_LOG" \
        N1_FIXTURE_ROOT="$FIXTURE" \
        "$@" ./scripts/n1-compat.sh HEAD) 2>&1 )" || LAST_STATUS=$?
}

# Every dotnet invocation of one phase against one suite, e.g. `dotnet test` on the Auth suite.
# Log line: <cwd>|<N1_COMPAT_SCHEMA_SCRIPTS_DIR>|<argv>
invocations_for() {
    grep -E "\|$1 " "$STUB_LOG" | grep -F "$2" || true
}

seam_dir_of() {
    printf '%s\n' "$1" | cut -d'|' -f2
}

CASE='everything green'
run_proof
[ "$LAST_STATUS" -eq 0 ] || fail 'a previous release that builds and passes must exit 0' "status $LAST_STATUS"
grep -q 'GREEN' <<<"$LAST_OUTPUT" || fail 'the green verdict should be stated'
[ -n "$(invocations_for test 'AuthSystem')" ] || fail 'both suites must actually run'
[ -n "$(invocations_for test 'TranslationSystem')" ] || fail 'both suites must actually run'
pass 'a buildable, passing previous release exits 0 and runs every discovered suite'

CASE='old suite tests fail'
run_proof STUB_TEST_FAIL_MATCH='AuthSystem'
[ "$LAST_STATUS" -eq 1 ] || fail 'tests that ran and failed are the exit-1 verdict' "status $LAST_STATUS"
grep -q 'RED' <<<"$LAST_OUTPUT" || fail 'the red verdict should be stated'
grep -qi 'backward-incompatible' <<<"$LAST_OUTPUT" || fail 'the red verdict must name the ADR-0023 resolution'
if grep -qi 'UNJUDGED\|COULD NOT RUN' <<<"$LAST_OUTPUT"; then fail 'a proven incompatibility must not read as an infra failure'; fi
pass 'a previous release whose TESTS fail on the new schema exits 1 as RED'

CASE='old suite does not restore'
run_proof STUB_RESTORE_FAIL_MATCH='AuthSystem'
[ "$LAST_STATUS" -eq 2 ] || fail 'a previous release that cannot restore proves nothing — exit 2' "status $LAST_STATUS"
grep -q 'COULD NOT RUN' <<<"$LAST_OUTPUT" || fail 'the operator must be told the proof could not run'
grep -q 'UNJUDGED' <<<"$LAST_OUTPUT" || fail 'the batch must read as unjudged, not proven bad'
if grep -qw 'RED' <<<"$LAST_OUTPUT"; then fail 'a restore failure must NOT be reported as a schema RED'; fi
if grep -qi 'backward-incompatible' <<<"$LAST_OUTPUT"; then fail 'a restore failure must not accuse the migrations'; fi
[ -z "$(invocations_for build 'AuthSystem')" ] || fail 'a suite that failed to restore must not be built'
[ -z "$(invocations_for test 'AuthSystem')" ] || fail 'a suite that failed to restore must not be tested'
pass 'a previous release that no longer restores exits 2 as UNJUDGED, and is never tested'

CASE='old suite does not build'
run_proof STUB_BUILD_FAIL_MATCH='TranslationSystem'
[ "$LAST_STATUS" -eq 2 ] || fail 'a previous release that cannot build proves nothing — exit 2' "status $LAST_STATUS"
grep -q 'UNJUDGED' <<<"$LAST_OUTPUT" || fail 'the batch must read as unjudged, not proven bad'
if grep -qw 'RED' <<<"$LAST_OUTPUT"; then fail 'a build failure must NOT be reported as a schema RED'; fi
[ -z "$(invocations_for test 'TranslationSystem')" ] || fail 'a suite that failed to build must not be tested'
[ -n "$(invocations_for test 'AuthSystem')" ] || fail 'one unbuildable suite must not stop the others from being proven'
pass 'a previous release that no longer builds exits 2 as UNJUDGED, and is never tested'

CASE='a proven incompatibility outranks an unbuildable sibling'
run_proof STUB_BUILD_FAIL_MATCH='AuthSystem' STUB_TEST_FAIL_MATCH='TranslationSystem'
[ "$LAST_STATUS" -eq 1 ] || fail 'a suite that ran and failed still proves the schema breaks' "status $LAST_STATUS"
grep -q 'RED' <<<"$LAST_OUTPUT" || fail 'the proven incompatibility is the verdict the operator must act on'
pass 'a real test failure wins over an unbuildable sibling suite — the batch IS proven bad'

CASE='the previous release is not re-judged by todays rules'
run_proof
# restore and build only: those are the two phases that resolve packages and compile, so they are
# the two that today's advisory feed and today's analyzers can fail. The run itself is --no-build.
for phase in restore build; do
    invocations="$(invocations_for "$phase" 'Tests.Integration')"
    [ -n "$invocations" ] || fail "the previous release must be ${phase}d at all"
    while IFS= read -r line; do
        grep -qF -- '-p:TreatWarningsAsErrors=false' <<<"$line" \
            || fail "every '$phase' of the previous release must waive TreatWarningsAsErrors — an advisory published after that release must not block a promotion (CD #236)" "$line"
    done <<<"$invocations"
done
pass 'every phase of the previous release waives TreatWarningsAsErrors'

CASE='the current tree keeps its own gate'
run_proof
head_restores="$(grep -E "^$FIXTURE\|[^|]*\|restore " "$STUB_LOG" || true)"
[ -n "$head_restores" ] || fail 'the current tree must still be restored'
if grep -qF -- '-p:TreatWarningsAsErrors=false' <<<"$head_restores"; then
    fail 'the waiver is for the previous release only — the CURRENT tree keeps its zero-warning gate' "$head_restores"
fi
pass 'the waiver never leaks onto the current tree'

CASE='the built output is reused'
run_proof
while IFS= read -r line; do
    grep -qF -- '--no-build' <<<"$line" || fail 'the run must reuse the build, so a test failure can only mean the tests failed' "$line"
done <<<"$(invocations_for test 'Tests.Integration')"
pass 'the suites run with --no-build, so exit 1 can only come from the tests themselves'

CASE='a suite that executed nothing proves nothing'
run_proof STUB_TEST_NO_TESTS_MATCH='AuthSystem'
[ "$LAST_STATUS" -eq 2 ] || fail 'a run that discovered zero tests must be UNJUDGED, never green' "status $LAST_STATUS"
grep -q 'UNJUDGED' <<<"$LAST_OUTPUT" || fail 'zero executed tests is an infra verdict'
if grep -q 'GREEN' <<<"$LAST_OUTPUT"; then fail 'a suite that ran nothing must NOT report the batch green'; fi
grep -qi 'ZERO tests' <<<"$LAST_OUTPUT" || fail 'the log must name the suite that executed nothing'
pass 'a suite discovering zero tests exits 2 — dotnet test alone would have exited 0'

CASE='the run carries the schema seam'
run_proof
# The dir the HEAD schema was generated INTO — whatever `dotnet ef migrations script --output` was
# pointed at. Asserted from the log, not from disk: the script deletes it on the way out.
generated_into=''
while IFS= read -r line; do
    out="$(printf '%s\n' "$line" | grep -o -- '--output [^ ]*' | cut -d' ' -f2)"
    [ -n "$out" ] || fail 'the schema scripts must be generated to an explicit --output path' "$line"
    case "$out" in
        */translation.sql | */auth.sql) ;;
        *) fail 'both contexts must be generated, one script each' "$out" ;;
    esac
    generated_into="$(dirname "$out")"
done <<<"$(invocations_for ef 'migrations script')"
[ -n "$generated_into" ] || fail 'the HEAD schema must be generated at all'

while IFS= read -r line; do
    seam="$(seam_dir_of "$line")"
    [ -n "$seam" ] || fail 'without N1_COMPAT_SCHEMA_SCRIPTS_DIR the old suite migrates its OWN old schema and every run is vacuous green (ADR-0024 §3)' "$line"
    [ "$seam" = "$generated_into" ] \
        || fail 'the run must be handed the very dir the HEAD schema was generated into' "seam=$seam generated=$generated_into"
done <<<"$(invocations_for test 'Tests.Integration')"
pass 'every run receives the seam dir the HEAD schema was generated into'

CASE='no suites to discover'
bare="$TMP_ROOT/bare"
cp -R "$FIXTURE" "$bare"
rm -rf "$bare/.git"
find "$bare/tests" -name '*.Tests.Integration.csproj' -delete
git -C "$bare" init --quiet --initial-branch=main
git -C "$bare" config user.email 'n1-compat-tests@example.invalid'
git -C "$bare" config user.name 'n1-compat tests'
git -C "$bare" add -A
git -C "$bare" commit --quiet -m 'fixture: previous release with no integration suites'
LAST_STATUS=0
LAST_OUTPUT="$( (cd "$bare" && env \
    PATH="$STUB_BIN:$PATH" \
    N1_STUB_LOG="$STUB_LOG" \
    N1_FIXTURE_ROOT="$bare" \
    ./scripts/n1-compat.sh HEAD) 2>&1 )" || LAST_STATUS=$?
[ "$LAST_STATUS" -eq 2 ] || fail 'a previous release with no integration suites must refuse to report green' "status $LAST_STATUS"
if grep -q 'GREEN' <<<"$LAST_OUTPUT"; then fail 'nothing discovered must never read as proven'; fi
pass 'a previous release with no integration suites exits 2 instead of a false green'

CASE='step summary'
run_proof STUB_RESTORE_FAIL_MATCH='AuthSystem'
grep -q 'UNJUDGED' "$TMP_ROOT/step_summary" || fail 'the approver reads the summary — the verdict must land there too'
pass 'a blocked proof writes its verdict to the job summary'

printf '\nAll %d n1-compat case(s) passed.\n' "$cases"
