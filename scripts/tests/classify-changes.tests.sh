#!/usr/bin/env bash
#
# Offline self-test for scripts/ci/classify-changes.sh — the classifier that decides whether a PR
# runs the .NET gate, the bash guards, the image build, or nothing at all.
#
# It runs UNCONDITIONALLY in the required "Pull Request Verification" job, before the classifier is
# trusted to classify anything: a classifier that mis-classifies real source as inert would silently
# skip the build and the tests, and no other check would notice. The false-green half of the table
# below is therefore the point of this file — the "skips the right things" half only guards the CI
# bill.
#
# Most cases feed a path list on stdin through the --files seam. The last group builds a scratch git
# repo instead and runs the classifier the way pr-verify does, because the seam never runs git, and
# git itself can hide a changed path: a moved file used to show only its new path (#1041).
set -uo pipefail

# With CDPATH set, `cd` to a relative path may print the folder, and that text would end up here (#1048).
SCRIPTS_DIR="$(CDPATH='' cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
CLASSIFY="$SCRIPTS_DIR/ci/classify-changes.sh"

# Two steps on purpose: `cd ""` succeeds, so a failed mktemp inside one $(cd …) would point the
# cleanup below at the current directory.
TMP_ROOT="$(mktemp -d)" || exit 1
TMP_ROOT="$(cd -- "$TMP_ROOT" && pwd -P)" || exit 1
[ -d "$TMP_ROOT" ] || exit 1
trap 'rm -rf "$TMP_ROOT"' EXIT

# Isolate from the developer's git config (gpg signing, hooks, defaults).
export GIT_CONFIG_GLOBAL=/dev/null GIT_CONFIG_SYSTEM=/dev/null
export GIT_AUTHOR_NAME=classify-changes-tests GIT_AUTHOR_EMAIL=tests@localhost
export GIT_COMMITTER_NAME=classify-changes-tests GIT_COMMITTER_EMAIL=tests@localhost

FIXTURE='tests/LotroKoniecDev.Tests.Unit/Fixtures/exported.txt'
SOURCE='src/Patcher/LotroKoniecDev.Domain/Result.cs'
GUARD='scripts/check-ssr-purity.sh'
DOCKERFILE='src/Frontend/LotroKoniecDev.Frontend/Dockerfile'
DOC='docs/adr/0035.md'

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

# pr_repo <change> → prints the path of a new scratch repo with two commits: a base that holds the
# files named at the top of this script, and the PR, made by running the shell snippet <change>.
pr_repo() {
    local change="$1" repo
    repo="$(mktemp -d "$TMP_ROOT/repo.XXXXXX")" || return 1
    [ -d "$repo" ] || return 1
    (
        cd -- "$repo" || exit 1
        git init -q -b main || exit 1
        mkdir -p "$(dirname "$FIXTURE")" "$(dirname "$SOURCE")" "$(dirname "$GUARD")" \
            "$(dirname "$DOCKERFILE")" "$(dirname "$DOC")" || exit 1
        printf '1||2||Hello||NULL||1||NULL||0123456789abcdef\n' > "$FIXTURE"
        printf 'namespace LotroKoniecDev.Domain;\n\npublic sealed class Result\n{\n}\n' > "$SOURCE"
        printf '#!/usr/bin/env bash\necho "SSR purity"\n' > "$GUARD"
        printf 'FROM mcr.microsoft.com/dotnet/aspnet:10.0\n' > "$DOCKERFILE"
        printf '# ADR-0035\n\nThe decision.\n' > "$DOC"
        git add -A && git commit -qm 'base' || exit 1
        eval "$change" || exit 1
        git add -A && git commit -qm 'the PR'
    ) > /dev/null 2>&1 || return 1
    printf '%s\n' "$repo"
}

# expect_git <expected-verdicts> <description> <change> [<folder>]
# Runs the classifier on the PR of pr_repo <change> as pr-verify does (`classify-changes.sh HEAD^1`),
# from <folder> of the scratch repo, the repo root by default.
expect_git() {
    local expected="$1" description="$2" change="$3" folder="${4-.}"
    local repo actual
    cases=$((cases + 1))
    if ! repo="$(pr_repo "$change")"; then
        printf 'FAIL  %s\n        could not build the scratch repo\n' "$description"
        failures=$((failures + 1))
        return
    fi
    actual="$(cd -- "$repo/$folder" && "$CLASSIFY" HEAD^1 2>/dev/null | tr '\n' ' ')"
    actual="${actual% }"
    if [ "$actual" = "$expected" ]; then
        printf 'PASS  %s\n' "$description"
    else
        printf 'FAIL  %s\n        expected: %s\n        actual:   %s\n' "$description" "$expected" "$actual"
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
expect 'code=false guards=false images=false' 'another workflow'              '.github/workflows/codeql.yml'
expect 'code=false guards=false images=false' 'the dev compose stack'         'compose.yaml'
expect 'code=false guards=false images=false' 'an env example'                '.env.example'

echo
echo '── guards only: the bash gates re-run, the .NET gate does not ────────────────────────────────'
expect 'code=false guards=true images=false' 'a script the provenance self-test executes' 'scripts/claude/work-ticket.sh'
expect 'code=false guards=true images=false' 'the loop conductor, which its self-test runs' 'scripts/claude/backlog-loop.sh'
expect 'code=false guards=true images=false' 'the conductor self-test'                   'scripts/tests/claude-loop-conductor.tests.sh'
expect 'code=false guards=true images=false' 'the terminal launcher, which its self-test runs' 'scripts/claude/start-loop.sh'
expect 'code=false guards=true images=false' 'the launcher self-test'                    'scripts/tests/claude-loop-launcher.tests.sh'
expect 'code=false guards=true images=false' 'the provenance gate itself'                 'scripts/claude/issue-trust.sh'
expect 'code=false guards=true images=false' 'the SSR-purity guard'                       'scripts/check-ssr-purity.sh'
expect 'code=false guards=true images=false' 'the hypermedia guard'                       'scripts/check-client-hypermedia.sh'
expect 'code=false guards=true images=false' 'the hypermedia guard PowerShell twin'       'scripts/check-client-hypermedia.ps1'
expect 'code=false guards=true images=false' 'the hypermedia guard self-test'             'scripts/tests/check-client-hypermedia.tests.sh'
expect 'code=false guards=true images=false' 'the migration-safety guard'                 'scripts/check-migration-safety.sh'
expect 'code=false guards=true images=false' 'a guard self-test'                          'scripts/tests/check-migration-safety.tests.sh'
expect 'code=false guards=true images=false' 'the restore-graph guard'                    'scripts/check-dockerfile-restore-graph.sh'
expect 'code=false guards=true images=false' 'the restore-graph guard PowerShell twin'    'scripts/check-dockerfile-restore-graph.ps1'
expect 'code=false guards=true images=false' 'the restore-graph guard self-test'          'scripts/tests/check-dockerfile-restore-graph.tests.sh'
expect 'code=false guards=true images=false' 'the script CD runs on a prod box'           'scripts/hetzner/deploy.sh'
expect 'code=false guards=true images=false' 'the rollout self-test'                      'scripts/tests/hetzner-deploy.tests.sh'
expect 'code=false guards=true images=false' 'the box bootstrap script'                   'scripts/hetzner/bootstrap.sh'
expect 'code=false guards=true images=false' 'the bootstrap swap self-test'               'scripts/tests/hetzner-bootstrap-swap.tests.sh'
expect 'code=false guards=true images=false' 'the bootstrap DOCKER-USER self-test'        'scripts/tests/hetzner-bootstrap-docker-user.tests.sh'
expect 'code=false guards=true images=false' 'the prod N-1 promotion gate resolver'       'scripts/ci/resolve-prod-baseline.sh'
expect 'code=false guards=true images=false' 'the resolver self-test'                     'scripts/tests/resolve-prod-baseline.tests.sh'
expect 'code=false guards=true images=false' 'the promotion gate verdict mapping'         'scripts/ci/n1-promotion-gate.sh'
expect 'code=false guards=true images=false' 'the verdict-mapping self-test'              'scripts/tests/n1-promotion-gate.tests.sh'
expect 'code=false guards=true images=false' 'the N-1 proof the gate calls'               'scripts/n1-compat.sh'
expect 'code=false guards=true images=false' 'the N-1 proof classification self-test'     'scripts/tests/n1-compat.tests.sh'

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
echo '── real git: every changed path reaches the rules, both ends of a move included (#1041) ──────'
# The blind spot the cases below guard: git's own default lists only where a moved file went. If git
# ever stops doing that, this case fails, and the move cases no longer test anything.
cases=$((cases + 1))
if repo="$(pr_repo "git mv $FIXTURE docs/exported.txt")" \
    && [ "$(git -C "$repo" diff --name-only HEAD^1 HEAD)" = 'docs/exported.txt' ]; then
    printf 'PASS  %s\n' 'plain git diff --name-only hides the old path of a move'
else
    printf 'FAIL  %s\n' 'plain git diff --name-only hides the old path of a move'
    failures=$((failures + 1))
fi
expect_git 'code=true guards=true images=false'   'a golden fixture moved into docs/'            "git mv $FIXTURE docs/exported.txt"
expect_git 'code=true guards=true images=false'   'a C# source file moved into docs/ as Markdown' "git mv $SOURCE docs/Result.md"
expect_git 'code=false guards=true images=false'  'a guard script moved into docs/'              "git mv $GUARD docs/check-ssr-purity.sh"
expect_git 'code=false guards=true images=true'   'a Dockerfile moved into docs/'                "git mv $DOCKERFILE docs/frontend-image.txt"
expect_git 'code=false guards=false images=false' 'a doc moved to another inert path'            "mkdir -p docs/adr/superseded && git mv $DOC docs/adr/superseded/0035.md"
expect_git 'code=true guards=true images=false'   'a doc moved into the code'                    "git mv $DOC src/Patcher/LotroKoniecDev.Domain/0035.txt"
expect_git 'code=true guards=true images=false'   'a move, with copy detection on in the config' "git config diff.renames copies && git mv $FIXTURE docs/exported.txt"
expect_git 'code=true guards=true images=false'   'a deleted build input'                         "git rm -q $SOURCE"
expect_git 'code=false guards=false images=false' 'a build input copied into docs/, original untouched' \
    "git config diff.renames copies && cp $FIXTURE docs/exported.txt"
expect_git 'code=true guards=true images=false'   'a copy into docs/ whose original is edited too' \
    "git config diff.renames copies && cp $SOURCE docs/Result.cs && printf '// edited\n' >> $SOURCE"
expect_git 'code=true guards=true images=false'   'diff.relative on, classified from inside docs/' \
    "git config diff.relative true && printf 'edited\n' >> $DOC && printf '// edited\n' >> $SOURCE" docs

echo
if [ "$failures" -gt 0 ]; then
    printf '%d/%d cases FAILED\n' "$failures" "$cases"
    exit 1
fi
printf 'all %d cases passed\n' "$cases"
