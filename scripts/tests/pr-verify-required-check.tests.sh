#!/usr/bin/env bash
#
# Offline self-test for the required job in .github/workflows/pr-verify.yml: the job named
# "Pull Request Verification", which the main ruleset requires (#791).
#
# GitHub counts a SKIPPED job as a passed required check. It skips a job when a job in its `needs:`
# fails or never starts, and when its job-level `if:` is false. So this job must start on every PR
# and decide inside itself what to run. This file proves that in two ways:
#
#   1. Shape. The job exists under that exact name. It has no `needs:`, no job-level `if:` and no
#      `continue-on-error`. The steps up to the classifier (`id: diff`) carry no `if:`. Every later
#      step has no `if:`, or skips only when its verdict is exactly 'false'. The workflow has no
#      `paths` filter. Broken copies of the workflow prove that each of these checks still fires.
#   2. Behavior. It runs the job's own steps up to the classifier in a scratch git repo, with a
#      deliberately broken classifier in place, the way the runner runs them. Then it replays every
#      later step's `if:` against the verdicts those steps wrote. A broken classifier must leave the
#      job red, or make the whole .NET gate run. Green with the gate skipped is the bug.
#
# The YAML reader understands the block style pr-verify.yml uses: two-space indent, one key per line,
# a one-line `run:` for every step up to the classifier. A layout it cannot read fails the test.
#
# Pure bash + awk + git. CI-only (Linux runners), like classify-changes.sh, so no .ps1 twin.
set -uo pipefail

REPO_ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd -P)"
WORKFLOW="$REPO_ROOT/.github/workflows/pr-verify.yml"
REQUIRED_CHECK='Pull Request Verification'
US=$'\037'

TMP_ROOT="$(cd "$(mktemp -d)" && pwd -P)"
trap 'rm -rf "$TMP_ROOT"' EXIT

# Isolate from the developer's git config (gpg signing, hooks, defaults).
export GIT_CONFIG_GLOBAL=/dev/null GIT_CONFIG_SYSTEM=/dev/null
export GIT_AUTHOR_NAME=pr-verify-tests GIT_AUTHOR_EMAIL=tests@localhost
export GIT_COMMITTER_NAME=pr-verify-tests GIT_COMMITTER_EMAIL=tests@localhost

failures=0
cases=0

pass() {
    cases=$((cases + 1))
    printf 'PASS  %s\n' "$1"
}

fail() {
    cases=$((cases + 1))
    failures=$((failures + 1))
    printf 'FAIL  %s\n' "$1"
    if [ -n "${2-}" ]; then
        printf '%s\n' "$2" | sed 's/^/        /'
    fi
}

# Prints one record per line, fields split by $US:
#   ON  <key>                          a paths / paths-ignore filter under `on:`
#   J   <job> <key> <value>            a job-level key
#   S   <job> <step> <key> <value>     a step key (step = 0-based index)
# Values lose a trailing YAML comment and their outer quotes. Q holds a single quote: awk flavors
# disagree on \x27, and the program itself sits in single quotes.
# shellcheck disable=SC2016 # awk program, not shell
READER='
function trim(s) { sub(/^[ \t]+/, "", s); sub(/[ \t\r]+$/, "", s); return s }
function clean(v) {
    v = trim(v)
    if (length(v) >= 2 && v ~ /^".*"$/) { return substr(v, 2, length(v) - 2) }
    if (length(v) >= 2 && substr(v, 1, 1) == Q && substr(v, length(v), 1) == Q) {
        v = substr(v, 2, length(v) - 2); gsub(Q Q, Q, v); return v
    }
    sub(/[ \t]+#.*$/, "", v)
    return trim(v)
}
function keyval(line) { KEY = line; sub(/:.*/, "", KEY); KEY = trim(KEY); VAL = line; sub(/^[^:]*:/, "", VAL); VAL = clean(VAL) }
/^[A-Za-z_"][^:]*:/ { section = $0; sub(/:.*/, "", section); gsub(/"/, "", section); job = ""; insteps = 0; next }
section == "on" && /^[ \t]+(paths|paths-ignore):/ { keyval($0); print "ON" US KEY; next }
section != "jobs" { next }
/^  [A-Za-z0-9_-]+:[ \t]*$/ { job = trim($0); sub(/:$/, "", job); step = -1; insteps = 0; next }
job == "" { next }
/^    [A-Za-z0-9_-]+:/ { keyval(substr($0, 5)); print "J" US job US KEY US VAL; insteps = (KEY == "steps"); next }
insteps && /^      - [A-Za-z0-9_-]+:/ { step++; keyval(substr($0, 9)); print "S" US job US step US KEY US VAL; next }
insteps && /^        [A-Za-z0-9_-]+:/ { keyval(substr($0, 9)); print "S" US job US step US KEY US VAL; next }
'

# Fills the REQ_* globals and the STEP_* arrays from the required job of <workflow>.
load_required_job() {
    local records kind f1 f2 f3 f4
    records="$(awk -v US="$US" -v Q="'" "$READER" "$1")"

    REQ_JOB=''
    while IFS="$US" read -r kind f1 f2 f3 f4; do
        if [ "$kind" = J ] && [ "$f2" = name ] && [ "$f3" = "$REQUIRED_CHECK" ]; then
            REQ_JOB="$f1"
            break
        fi
    done <<< "$records"

    ON_FILTERS=''
    REQ_JOB_KEYS=''
    STEP_COUNT=0
    STEP_NAME=(); STEP_ID=(); STEP_IF=(); STEP_RUN=(); STEP_USES=(); STEP_COE=()
    while IFS="$US" read -r kind f1 f2 f3 f4; do
        case "$kind" in
            ON) ON_FILTERS="$ON_FILTERS $f1" ;;
            J)  [ "$f1" = "$REQ_JOB" ] && REQ_JOB_KEYS="$REQ_JOB_KEYS $f2" ;;
            S)
                [ "$f1" = "$REQ_JOB" ] || continue
                [ "$f2" -ge "$STEP_COUNT" ] && STEP_COUNT=$((f2 + 1))
                case "$f3" in
                    name)              STEP_NAME[f2]="$f4" ;;
                    id)                STEP_ID[f2]="$f4" ;;
                    if)                STEP_IF[f2]="$f4" ;;
                    run)               STEP_RUN[f2]="$f4" ;;
                    uses)              STEP_USES[f2]="$f4" ;;
                    continue-on-error) STEP_COE[f2]="$f4" ;;
                esac
                ;;
        esac
    done <<< "$records"

    DIFF_IDX=-1
    local i
    for ((i = 0; i < STEP_COUNT; i++)); do
        if [ "${STEP_ID[i]-}" = diff ]; then
            DIFF_IDX=$i
            break
        fi
    done
}

# `${{ x }}` → `x`, runs of spaces → one space.
normalize_condition() {
    printf '%s' "$1" | sed -e 's/^\${{[[:space:]]*//' -e 's/[[:space:]]*}}$//' | tr -s ' '
}

# The one condition allowed after the classifier. It skips a step only on an explicit 'false', so a
# verdict that is missing, empty or garbled runs the step.
FAIL_CLOSED_CONDITION="^steps\.diff\.outputs\.[a-z]+ != 'false'$"

# Prints every shape problem of <workflow>, one per line; prints nothing when the shape is right.
shape_problems() {
    load_required_job "$1"
    if [ -z "$REQ_JOB" ]; then
        echo "no job is named '$REQUIRED_CHECK' — the ruleset requires exactly that name"
        return
    fi
    local key i condition gated=0
    for key in $ON_FILTERS; do
        echo "on: carries a '$key' filter — a run filtered out never reports the required check (#255)"
    done
    for key in $REQ_JOB_KEYS; do
        case "$key" in
            needs)             echo "the required job has needs: — a failed or unstarted job there skips it, and skipped counts as passed" ;;
            if)                echo "the required job has a job-level if: — when it is false the job is skipped, and skipped counts as passed" ;;
            continue-on-error) echo "the required job has continue-on-error — a failed step would still report success" ;;
        esac
    done
    if [ "$DIFF_IDX" -lt 0 ]; then
        echo "no step has id: diff — nothing classifies the PR inside the required job"
        return
    fi
    for ((i = 0; i < STEP_COUNT; i++)); do
        if [ -n "${STEP_COE[i]-}" ]; then
            echo "step '${STEP_NAME[i]-#$i}' has continue-on-error — its failure would not turn the check red"
        fi
        if [ "$i" -le "$DIFF_IDX" ]; then
            if [ -n "${STEP_IF[i]-}" ]; then
                echo "step '${STEP_NAME[i]-#$i}' runs up to the classification but has an if: — it must always run"
            fi
            case "${STEP_USES[i]-}" in
                '' | actions/checkout@*) ;;
                *) echo "step '${STEP_NAME[i]-#$i}' runs an action before the classification — the replay below cannot run it" ;;
            esac
            case "${STEP_RUN[i]-}" in
                '|'* | '>'*) echo "step '${STEP_NAME[i]-#$i}' has a multi-line run: before the classification — keep it on one line so the replay can run it" ;;
            esac
            continue
        fi
        [ -n "${STEP_IF[i]-}" ] || continue
        condition="$(normalize_condition "${STEP_IF[i]}")"
        if ! printf '%s' "$condition" | grep -Eq "$FAIL_CLOSED_CONDITION"; then
            echo "step '${STEP_NAME[i]-#$i}' has if: $condition — after the classification only steps.diff.outputs.<verdict> != 'false' is allowed, so a missing verdict runs the step"
        fi
        case "$condition" in *outputs.code*) gated=1 ;; esac
    done
    if [ "$gated" -eq 0 ]; then
        echo "no step after the classification is gated on the code verdict — the replay below would prove nothing"
    fi
}

# expect_shape_problem <description> <expected-text> <workflow>
expect_shape_problem() {
    local description="$1" expected="$2" problems
    problems="$(shape_problems "$3")"
    if printf '%s' "$problems" | grep -qF -- "$expected"; then
        pass "$description"
    else
        fail "$description" "expected a problem containing: $expected"$'\n'"got: ${problems:-<none>}"
    fi
}

# mutate <name> <awk program> → path of a copy of the real workflow run through the program
# (Q holds a single quote, as in READER).
mutate() {
    local out="$TMP_ROOT/$1.yml"
    awk -v Q="'" "$2" "$WORKFLOW" > "$out"
    printf '%s' "$out"
}

# insert_after <name> <exact line> <new line> → path of a copy with <new line> after the first match.
insert_after() {
    local out="$TMP_ROOT/$1.yml"
    awk -v anchor="$2" -v added="$3" '{ print } !done && $0 == anchor { print added; done = 1 }' "$WORKFLOW" > "$out"
    printf '%s' "$out"
}

# Writes the classifier for <flavor> to <path>. `healthy` is the real one; every other flavor is a
# way a classifier can break.
install_classifier() {
    local flavor="$1" target="$2"
    local real="$REPO_ROOT/scripts/ci/classify-changes.sh"
    local dir
    dir="$(dirname "$target")"
    rm -f "$target"
    case "$flavor" in
        healthy)
            cp "$real" "$target"
            ;;
        crashes)
            printf '#!/usr/bin/env bash\necho "classifier: boom" >&2\nexit 1\n' > "$target"
            ;;
        dies-after-its-verdicts)
            cp "$real" "$target"
            printf 'if then\n' >> "$target"
            ;;
        missing)
            return 0
            ;;
        calls-everything-inert)
            cat > "$target" <<'EOF'
#!/usr/bin/env bash
printf 'code=false\nguards=false\nimages=false\n'
if [ -n "${GITHUB_OUTPUT-}" ]; then
    printf 'code=false\nguards=false\nimages=false\n' >> "$GITHUB_OUTPUT"
fi
EOF
            ;;
        writes-no-verdicts)
            cp "$real" "$dir/classify-changes.real.sh"
            cat > "$target" <<'EOF'
#!/usr/bin/env bash
unset GITHUB_OUTPUT
exec "$(dirname "$0")/classify-changes.real.sh" "$@"
EOF
            ;;
        garbles-its-verdicts)
            cp "$real" "$dir/classify-changes.real.sh"
            cat > "$target" <<'EOF'
#!/usr/bin/env bash
GITHUB_OUTPUT=/dev/null "$(dirname "$0")/classify-changes.real.sh" "$@" || exit
if [ -n "${GITHUB_OUTPUT-}" ]; then
    printf 'code=\nguards=maybe\nimages=\n' >> "$GITHUB_OUTPUT"
fi
EOF
            ;;
        *)
            echo "unknown classifier flavor: $flavor" >&2
            return 1
            ;;
    esac
    chmod +x "$target"
}

# output_value <verdict> <GITHUB_OUTPUT file> → the last value written for it, lower-cased (GitHub
# compares strings without regard to case).
output_value() {
    grep "^$1=" "$2" | tail -n 1 | cut -d= -f2- | tr '[:upper:]' '[:lower:]'
}

# replay <flavor> <changed path> → prints `red`, `gate-ran` or `gate-skipped`.
# A PR that changes <changed path> runs the required job's steps up to the classifier, with the
# <flavor> classifier in place, exactly as the runner runs a `run:` step (bash -e). A failed step ends
# the job red. Otherwise every later step's if: is replayed against the verdicts those steps wrote.
replay() {
    local flavor="$1" changed="$2" repo output log i rc condition verdict value gate_ran=1
    repo="$(mktemp -d "$TMP_ROOT/replay.XXXXXX")"
    output="$repo.github-output"
    log="$repo.log"
    : > "$output"

    cp -R "$REPO_ROOT/scripts" "$repo/scripts"
    install_classifier "$flavor" "$repo/scripts/ci/classify-changes.sh" || return 1
    printf 'base\n' > "$repo/README.md"
    git -C "$repo" init -q -b main
    git -C "$repo" add -A
    git -C "$repo" commit -qm 'base'
    mkdir -p "$(dirname "$repo/$changed")"
    printf 'change\n' > "$repo/$changed"
    git -C "$repo" add -A
    git -C "$repo" commit -qm 'the PR'

    for ((i = 0; i <= DIFF_IDX; i++)); do
        [ -n "${STEP_RUN[i]-}" ] || continue
        (cd "$repo" && GITHUB_OUTPUT="$output" GITHUB_STEP_SUMMARY=/dev/null \
            bash --noprofile --norc -e -c "${STEP_RUN[i]}") >> "$log" 2>&1
        rc=$?
        if [ "$rc" -ne 0 ]; then
            echo red
            return 0
        fi
    done

    for ((i = DIFF_IDX + 1; i < STEP_COUNT; i++)); do
        condition="$(normalize_condition "${STEP_IF[i]-}")"
        case "$condition" in *outputs.code*) ;; *) continue ;; esac
        verdict="${condition#steps.diff.outputs.}"
        verdict="${verdict%% *}"
        value="$(output_value "$verdict" "$output")"
        if [ "$value" = false ]; then
            gate_ran=0
        fi
    done
    if [ "$gate_ran" -eq 1 ]; then
        echo gate-ran
    else
        echo gate-skipped
    fi
}

# expect_replay <expected> <description> <flavor> <changed path>
expect_replay() {
    local expected="$1" description="$2" actual
    actual="$(replay "$3" "$4")"
    if [ "$actual" = "$expected" ]; then
        pass "$description"
    else
        fail "$description" "expected: $expected"$'\n'"actual:   $actual"
    fi
}

echo '── the real workflow has the fail-closed shape ──────────────────────────────────────────────'
problems="$(shape_problems "$WORKFLOW")"
if [ -z "$problems" ]; then
    pass "pr-verify.yml: '$REQUIRED_CHECK' always starts and classifies inside itself"
else
    fail "pr-verify.yml: '$REQUIRED_CHECK' always starts and classifies inside itself" "$problems"
fi

echo
echo '── each shape check still fires on a broken copy ───────────────────────────────────────────'
expect_shape_problem 'a helper job in needs: (the pre-#791 shape)' 'has needs:' \
    "$(insert_after needs "    name: $REQUIRED_CHECK" '    needs: changes')"
expect_shape_problem 'a job-level if:' 'job-level if:' \
    "$(insert_after job-if "    name: $REQUIRED_CHECK" "    if: github.event.pull_request.draft == false")"
expect_shape_problem 'continue-on-error on the job' 'the required job has continue-on-error' \
    "$(insert_after job-coe "    name: $REQUIRED_CHECK" '    continue-on-error: true')"
expect_shape_problem 'continue-on-error on the build step' "step 'Build' has continue-on-error" \
    "$(insert_after step-coe '      - name: Build' '        continue-on-error: true')"
expect_shape_problem 'an if: on the classification step' "step 'Classify changed paths' runs up to the classification but has an if:" \
    "$(insert_after classify-if '      - name: Classify changed paths' "        if: github.actor != 'dependabot[bot]'")"
# shellcheck disable=SC2016 # an awk program, not shell
expect_shape_problem "a gate step that runs only on an explicit 'true'" "if: steps.diff.outputs.code == 'true'" \
    "$(mutate open-condition '!done && $0 == "        if: steps.diff.outputs.code != " Q "false" Q { sub("!= " Q "false" Q, "== " Q "true" Q); done = 1 } { print }')"
expect_shape_problem 'a paths-ignore filter on the trigger' "carries a 'paths-ignore' filter" \
    "$(insert_after paths-ignore '    branches: ["main"]' "    paths-ignore: ['docs/**']")"
# shellcheck disable=SC2016 # an awk program, not shell
expect_shape_problem 'the required job renamed' 'no job is named' \
    "$(mutate renamed '$0 == "    name: Pull Request Verification" { print "    name: PR Verification"; next } { print }')"
# shellcheck disable=SC2016 # an awk program, not shell
expect_shape_problem 'no classification step at all' 'no step has id: diff' \
    "$(mutate no-diff '$0 == "        id: diff" { next } { print }')"

load_required_job "$WORKFLOW"
if [ -n "$problems" ] || [ "$DIFF_IDX" -lt 0 ]; then
    echo
    echo 'the replay needs the real workflow in the right shape — skipped until the cases above pass'
    printf '\n%d/%d cases FAILED\n' "$failures" "$cases"
    exit 1
fi

echo
echo '── the replay tells a skipped gate from a run one ──────────────────────────────────────────'
expect_replay gate-skipped 'healthy classifier, docs-only PR: green with the gate skipped (#255 stays fixed)' healthy 'docs/notes.md'
expect_replay gate-ran     'healthy classifier, a C# change: the gate runs'                                 healthy 'src/Patcher/LotroKoniecDev.Domain/Result.cs'

echo
echo '── a broken classifier on a docs-only PR never ends green with the gate skipped ──────────────'
expect_replay red      'it crashes before any verdict: the required check is red'                 crashes                 'docs/notes.md'
expect_replay red      'it writes its verdicts and then dies: red'                                dies-after-its-verdicts 'docs/notes.md'
expect_replay red      'the classifier file is gone: red'                                         missing                 'docs/notes.md'
expect_replay red      'it calls every path inert: its self-test turns the check red'             calls-everything-inert  'docs/notes.md'
expect_replay gate-ran 'it prints verdicts but never writes them to the step output: the gate runs' writes-no-verdicts    'docs/notes.md'
expect_replay gate-ran 'it writes empty or garbled verdicts: the gate runs'                       garbles-its-verdicts    'docs/notes.md'

echo
if [ "$failures" -gt 0 ]; then
    printf '%d/%d cases FAILED\n' "$failures" "$cases"
    exit 1
fi
printf 'all %d cases passed\n' "$cases"
