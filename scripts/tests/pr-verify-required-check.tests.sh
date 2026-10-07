#!/usr/bin/env bash
#
# Offline self-test for the required job in .github/workflows/pr-verify.yml: the job named
# "Pull Request Verification", which the main ruleset requires (#791).
#
# GitHub counts a SKIPPED job as a passed required check. It skips a job when a job in its `needs:`
# fails or never starts, and when its job-level `if:` is false. So this job must start on every PR
# and decide inside itself what to run. This file proves that in two ways:
#
#   1. Shape. Exactly one job, in all workflows, carries that name. It has no `needs:`, no
#      job-level `if:` and no `continue-on-error`. The steps up to the classifier (`id: diff`) carry
#      no `if:`. Every later step has no `if:`, or skips only when its verdict (code or guards) is
#      exactly 'false', and a guard script never waits on code. The workflow has no `paths` filter.
#      The `gitleaks` required check gets the same start checks, its one job name is counted, and
#      none of its steps may carry an `if:` or `continue-on-error`. Broken copies of the workflows
#      prove that each check still fires.
#   2. Behavior. It runs the job's own steps up to the classifier in a scratch git repo, with a
#      deliberately broken classifier in place, the way the runner runs them. Then it replays every
#      later step's `if:` against the verdicts the classifier wrote. A broken classifier must leave
#      the job red, or make every gated step run. Green with the gate skipped is the bug.
#
# The YAML reader understands the block style pr-verify.yml uses: two-space indent, one key per
# line, a one-line `run:` for every step up to the classifier. Quoted keys are fine. A form it
# cannot follow (a YAML anchor, alias or merge key, a job written on one line, a value that goes on
# in the next line or starts there, no `on:` section) fails the test instead of being guessed at.
# The required name is also searched as plain text, so a second job with it is found in any layout.
#
# It runs in pr-verify itself, in actionlint.yml (so a change that skips the whole required job
# still meets it) and in ci.yml on main. Pure bash + awk + git. CI-only (Linux runners), like
# classify-changes.sh, so no .ps1 twin.
set -uo pipefail

REPO_ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd -P)"
WORKFLOW="$REPO_ROOT/.github/workflows/pr-verify.yml"
REQUIRED_CHECK='Pull Request Verification'
US=$'\037'

# Two steps on purpose: `cd ""` succeeds, so a failed mktemp inside one $(cd …) would point the
# cleanup below at the current directory.
TMP_ROOT="$(mktemp -d)" || exit 1
TMP_ROOT="$(cd -- "$TMP_ROOT" && pwd -P)" || exit 1
[ -d "$TMP_ROOT" ] || exit 1
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
#   SEC <name>                         a top-level key (on, jobs, …)
#   ON  <key>                          a paths / paths-ignore filter under `on:`
#   J   <job> <key> <value>            a job-level key
#   S   <job> <step> <key> <value>     a step key (step = 0-based index)
#   X   <what>                         a line in a form the reader cannot follow
# Keys and values lose their outer quotes; values also lose a trailing YAML comment. Q holds a
# single quote: awk flavors disagree on \x27, and the program itself sits in single quotes.
# shellcheck disable=SC2016 # awk program, not shell
READER='
function trim(s) { sub(/^[ \t]+/, "", s); sub(/[ \t\r]+$/, "", s); return s }
function quoted(v) {
    return length(v) >= 2 && ((substr(v, 1, 1) == "\"" && substr(v, length(v), 1) == "\"") || (substr(v, 1, 1) == Q && substr(v, length(v), 1) == Q))
}
function unquote(v,    q) {
    if (!quoted(v)) { return v }
    q = substr(v, 1, 1)
    v = substr(v, 2, length(v) - 2)
    if (q == Q) { gsub(Q Q, Q, v) }
    return v
}
function clean(v) {
    v = trim(v)
    if (quoted(v)) { return unquote(v) }
    sub(/[ \t]+#.*$/, "", v)
    return trim(v)
}
function keyval(line) { KEY = line; sub(/:.*/, "", KEY); KEY = unquote(trim(KEY)); VAL = line; sub(/^[^:]*:/, "", VAL); VAL = clean(VAL) }
BEGIN { block = -1; cont = -1; nested = -1 }
# Every line passes here first. A block scalar (`run: |`) is skipped whole. A plain value that goes
# on in a deeper line is one value in YAML, but the rules below would read only its first line, so
# such a line is reported instead: `if: a` + `&& b`, or `run: x` + `|| true`, would hide from them.
# The same holds for a value that starts on the line after its key (`if:` + `a && b`): only a
# nested key or list item may follow a key with nothing after it.
/^[ \t]*$/ || /^[ \t]*#/ { next }
{
    indent = match($0, /[^ ]/) - 1
    if (block >= 0 && indent > block) { next }
    block = -1
    if (cont >= 0 && indent > cont) { print "X" US "line " NR " goes on with the value above it"; next }
    rest = substr($0, indent + 1); keyindent = indent; item = 0
    if (rest ~ /^-[ \t][ \t]/ || rest ~ /^-\t/) { print "X" US "line " NR " puts more than one space after a list dash"; next }
    if (rest ~ /^- /) { rest = substr(rest, 3); keyindent = indent + 2; item = 1 }
    iskey = (rest ~ /^[^:]*:([ \t]|$)/)
    if (nested >= 0 && indent > nested && !iskey && !item) {
        print "X" US "line " NR " is the value of the key above it, written on its own line"
        nested = -1
        next
    }
    nested = -1
    cont = keyindent
    if (iskey) {
        keyval(rest)
        if (VAL ~ /^[|>][-+0-9]*$/) { block = keyindent; cont = -1 }
        else if (VAL == "") { cont = -1; nested = keyindent }
    }
}
/^[^ \t#]/ {
    keyval($0); section = KEY; job = ""; insteps = 0; print "SEC" US section
    if (section == "on" && VAL ~ /paths/) { print "ON" US (VAL ~ /paths-ignore/ ? "paths-ignore" : "paths") }
    next
}
section == "on" && /paths/ { print "ON" US ($0 ~ /paths-ignore/ ? "paths-ignore" : "paths"); next }
section != "jobs" { next }
/^  [^ \t#]/ { keyval(substr($0, 3)); job = KEY; step = -1; insteps = 0; if (VAL != "") { print "X" US "job " job " is written on one line" }; next }
job == "" { next }
/^    [^ \t#]/ { keyval(substr($0, 5)); print "J" US job US KEY US VAL; insteps = (KEY == "steps"); next }
insteps && /^      - / { step++; keyval(substr($0, 9)); print "S" US job US step US KEY US VAL; next }
insteps && /^        [^ \t#]/ {
    if (step < 0) { print "X" US "a step key of job " job " sits before its first step"; next }
    keyval(substr($0, 9)); print "S" US job US step US KEY US VAL; next
}
'

# A key the reader trusts. Anything else (a merge key `<<`, an alias, an indentless list item) could
# hide a `needs:` or an `if:` from it.
SAFE_KEY='^[A-Za-z0-9_-]+$'

# note_unreadable <where> <key> <value>
note_unreadable() {
    if ! printf '%s' "$2" | grep -Eq "$SAFE_KEY"; then
        UNREADABLE="$UNREADABLE$1 has a key the reader cannot follow: $2"$'\n'
    fi
    case "$3" in
        '*'* | '&'*) UNREADABLE="$UNREADABLE$1 uses a YAML alias or anchor in $2"$'\n' ;;
    esac
    # The checks below read an empty value as "no such key", so an empty one must not get that far.
    case "$2" in
        if | continue-on-error | needs | run | uses | id)
            [ -n "$3" ] || UNREADABLE="$UNREADABLE$1 has an empty $2"$'\n' ;;
    esac
}

# load_required_job <workflow> [<check name>] fills the REQ_* globals and the STEP_* arrays from the
# job of <workflow> that carries <check name> (default: the one this file is about).
load_required_job() {
    local want="${2:-$REQUIRED_CHECK}" records kind f1 f2 f3 f4
    records="$(awk -v US="$US" -v Q="'" "$READER" "$1")"

    REQ_JOB=''
    while IFS="$US" read -r kind f1 f2 f3 f4; do
        if [ "$kind" = J ] && [ "$f2" = name ] && [ "$f3" = "$want" ]; then
            REQ_JOB="$f1"
            break
        fi
    done <<< "$records"

    SECTIONS=' '
    UNREADABLE=''
    ON_FILTERS=''
    REQ_JOB_KEYS=' '
    STEP_COUNT=0
    STEP_NAME=(); STEP_ID=(); STEP_IF=(); STEP_RUN=(); STEP_USES=(); STEP_COE=()
    while IFS="$US" read -r kind f1 f2 f3 f4; do
        case "$kind" in
            SEC) SECTIONS="$SECTIONS$f1 " ;;
            X)   UNREADABLE="$UNREADABLE$f1"$'\n' ;;
            ON)  ON_FILTERS="$ON_FILTERS $f1" ;;
            J)
                [ "$f1" = "$REQ_JOB" ] || continue
                REQ_JOB_KEYS="$REQ_JOB_KEYS$f2 "
                note_unreadable 'the required job' "$f2" "$f3"
                if [ "$f2" = steps ] && [ -n "$f3" ]; then
                    UNREADABLE="${UNREADABLE}the required job's steps: is not a block list"$'\n'
                fi
                ;;
            S)
                [ "$f1" = "$REQ_JOB" ] || continue
                note_unreadable "step #$f2 of the required job" "$f3" "$f4"
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

# required_name_lines <workflow>... → every line, outside comments, that names the required check,
# except a workflow's own top-level `name:`. Plain text on purpose, so a second job with that name is
# found whatever its indentation or YAML form. A name built by an expression (`${{ format(…) }}`) is
# not: that would be deliberate hiding, and review is the guard for it. A line that names the check
# for another reason (a workflow_run trigger, say) counts too: read the hit, then decide.
required_name_lines() {
    grep -nF -- "$REQUIRED_CHECK" "$@" /dev/null | grep -Ev '^[^:]+:[0-9]+:([[:space:]]*#|name:)' || true
}

# start_problems <workflow> <check name> → every reason the job carrying <check name> could be
# skipped or report success without its steps, one per line; nothing when it always starts.
start_problems() {
    load_required_job "$1" "$2"
    if [ -n "$UNREADABLE" ]; then
        printf '%s' "$UNREADABLE" | sed 's/$/ — so this test cannot prove the shape/'
    fi
    case "$SECTIONS" in
        *' on '*) ;;
        *) echo "the reader found no on: section — so it cannot check the trigger for a paths filter" ;;
    esac
    if [ -z "$REQ_JOB" ]; then
        echo "no job is named '$2' — the ruleset requires exactly that name"
        return
    fi
    local key
    for key in $ON_FILTERS; do
        echo "on: carries a '$key' filter — a run filtered out never reports the required check (#285)"
    done
    case "$REQ_JOB_KEYS" in *' needs '*)
        echo "the required job has needs: — a failed or unstarted job there skips it, and skipped counts as passed" ;;
    esac
    case "$REQ_JOB_KEYS" in *' if '*)
        echo "the required job has a job-level if: — when it is false the job is skipped, and skipped counts as passed" ;;
    esac
    case "$REQ_JOB_KEYS" in *' continue-on-error '*)
        echo "the required job has continue-on-error — a failed step would still report success" ;;
    esac
}

# Prints every shape problem of <workflow>, one per line; prints nothing when the shape is right.
shape_problems() {
    start_problems "$1" "$REQUIRED_CHECK"
    [ -n "$REQ_JOB" ] || return
    local named
    named="$(required_name_lines "$1" | grep -c .)"
    if [ "$named" != 1 ]; then
        echo "$named lines outside comments name '$REQUIRED_CHECK' — only the one job may carry it, a skipped second one would report the check as passed"
    fi
    if [ "$DIFF_IDX" -lt 0 ]; then
        echo "no step has id: diff — nothing classifies the PR inside the required job"
        return
    fi
    local i condition verdict gated=0
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
            continue
        fi
        verdict="${condition#steps.diff.outputs.}"
        verdict="${verdict%% *}"
        # images can be false while code is true, so a step gated on it skips the gate on a code PR.
        # A guard script gated on code skips on a PR that touches only that script (code=false). Only a
        # one-line run: is visible here; a multi-line `run: |` is not read by the reader.
        case "$verdict" in
            code)   gated=1 ;;
            guards) ;;
            *)      echo "step '${STEP_NAME[i]-#$i}' is gated on the '$verdict' verdict — the required job may use only code and guards" ;;
        esac
        case "$verdict:${STEP_RUN[i]-}" in
            code:*scripts/*) echo "step '${STEP_NAME[i]-#$i}' runs a guard script but is gated on code — it would skip on a PR that changes only that script" ;;
        esac
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

# insert_after_in <workflow> <name> <exact line> <new line> → path of a copy of <workflow> with
# <new line> after the first match.
insert_after_in() {
    local out="$TMP_ROOT/$2.yml"
    awk -v anchor="$3" -v added="$4" '{ print } !done && $0 == anchor { print added; done = 1 }' "$1" > "$out"
    printf '%s' "$out"
}

# insert_after <name> <exact line> <new line> → the same, on pr-verify.yml.
insert_after() {
    insert_after_in "$WORKFLOW" "$@"
}

# expect_required_name_lines <none|found> <description> <workflow>...
expect_required_name_lines() {
    local expected="$1" description="$2" hits
    shift 2
    hits="$(required_name_lines "$@")"
    if [ "$expected" = none ] && [ -z "$hits" ]; then
        pass "$description"
    elif [ "$expected" = found ] && [ -n "$hits" ]; then
        pass "$description"
    else
        fail "$description" "expected: $expected"$'\n'"hits: ${hits:-<none>}"
    fi
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
# <flavor> classifier in place, as the runner runs a `run:` step: bash -e, and a GITHUB_OUTPUT file
# of its own for each step. A failed step ends the job red. Otherwise every later step's if: is
# replayed against the verdicts the classifier step wrote. `gate-ran` means every gated step runs.
replay() {
    local flavor="$1" changed="$2" repo log i rc condition verdict value gate_ran=1
    repo="$(mktemp -d "$TMP_ROOT/replay.XXXXXX")" || return 1
    [ -d "$repo" ] || return 1
    log="$repo.log"

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
        : > "$repo.output.$i"
        [ -n "${STEP_RUN[i]-}" ] || continue
        (cd "$repo" && GITHUB_OUTPUT="$repo.output.$i" GITHUB_STEP_SUMMARY=/dev/null \
            bash --noprofile --norc -e -c "${STEP_RUN[i]}") >> "$log" 2>&1
        rc=$?
        if [ "$rc" -ne 0 ]; then
            echo red
            return 0
        fi
    done

    for ((i = DIFF_IDX + 1; i < STEP_COUNT; i++)); do
        [ -n "${STEP_IF[i]-}" ] || continue
        condition="$(normalize_condition "${STEP_IF[i]}")"
        verdict="${condition#steps.diff.outputs.}"
        verdict="${verdict%% *}"
        value="$(output_value "$verdict" "$repo.output.$DIFF_IDX")"
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
expect_shape_problem 'a second job with the required name, which needs the first' "2 lines outside comments name '$REQUIRED_CHECK'" \
    "$(mutate second-job '{ print } END { print "  shadow:"; print "    name: Pull Request Verification"; print "    needs: build"; print "    if: failure()" }')"
expect_shape_problem 'a second job indented differently, which the reader cannot see' "2 lines outside comments name '$REQUIRED_CHECK'" \
    "$(mutate second-job-deeper '{ print } END { print "  shadow:"; print "      name: Pull Request Verification"; print "      needs: build"; print "      if: failure()"; print "      runs-on: ubuntu-24.04"; print "      steps:"; print "        - run: \"true\"" }')"
expect_shape_problem 'a second job whose name is a block scalar' "2 lines outside comments name '$REQUIRED_CHECK'" \
    "$(mutate second-job-folded-name '{ print } END { print "  shadow:"; print "    name: >-"; print "      Pull Request Verification"; print "    needs: build"; print "    if: failure()" }')"
# shellcheck disable=SC2016 # an awk program, not shell
expect_shape_problem 'extra spaces after a step dash fail the test' 'more than one space after a list dash' \
    "$(mutate dash-spaces '$0 == "      - name: Run Unit Tests" { print "      -   name: Run Unit Tests"; next } { print }')"
expect_shape_problem 'the .NET steps gated on the images verdict' "is gated on the 'images' verdict" \
    "$(mutate dotnet-on-images '{ gsub("steps[.]diff[.]outputs[.]code", "steps.diff.outputs.images"); print }')"
# shellcheck disable=SC2016 # an awk program, not shell
expect_shape_problem 'a guard script gated on the code verdict' 'runs a guard script but is gated on code' \
    "$(mutate guard-on-code '!done && $0 == "        if: steps.diff.outputs.guards != " Q "false" Q { print "        if: steps.diff.outputs.code != " Q "false" Q; done = 1; next } { print }')"
# shellcheck disable=SC2016 # an awk program, not shell
expect_shape_problem 'a guard script run through bash, gated on the code verdict' 'runs a guard script but is gated on code' \
    "$(mutate guard-via-bash-on-code '$0 == "      - name: Frontend markup guard" { g = 1 } g && $0 == "        if: steps.diff.outputs.guards != " Q "false" Q { print "        if: steps.diff.outputs.code != " Q "false" Q; next } g && $0 == "        run: ./scripts/check-ssr-purity.sh" { print "        run: bash ./scripts/check-ssr-purity.sh"; g = 0; next } { print }')"

echo
echo '── the reader does not lose a key to YAML syntax ──────────────────────────────────────────'
expect_shape_problem 'a quoted job key still counts' 'has needs:' \
    "$(insert_after quoted-needs "    name: $REQUIRED_CHECK" '    "needs": changes')"
# shellcheck disable=SC2016 # an awk program, not shell
expect_shape_problem 'a quoted on: key still has its paths filter read' "carries a 'paths-ignore' filter" \
    "$(mutate quoted-on '$0 == "on:" { print Q "on" Q ":"; next } { print } $0 == "    branches: [\"main\"]" { print "    paths-ignore: [docs]" }')"
expect_shape_problem 'a merge key that could carry needs: fails the test' 'cannot follow: <<' \
    "$(insert_after merge-key "    name: $REQUIRED_CHECK" '    <<: *gate')"
expect_shape_problem 'an alias for a step condition fails the test' 'uses a YAML alias or anchor in if' \
    "$(insert_after alias-if '      - name: Build' '        if: *only-on-code')"
expect_shape_problem 'a gate condition that goes on in the next line fails the test' 'goes on with the value above it' \
    "$(insert_after if-continued "        if: steps.diff.outputs.code != 'false'" "          && github.actor != 'dependabot[bot]'")"
expect_shape_problem 'a classifier self-test command that goes on in the next line fails the test' 'goes on with the value above it' \
    "$(insert_after run-continued '        run: ./scripts/tests/classify-changes.tests.sh' '          || true')"
# shellcheck disable=SC2016 # an awk program, not shell
expect_shape_problem 'a gate condition that starts on the line after if: fails the test' 'is the value of the key above it' \
    "$(mutate if-next-line '$0 == "      - name: Build" { build = 1 } build && $0 == "        if: steps.diff.outputs.code != " Q "false" Q { print "        if:"; print "          steps.diff.outputs.code != " Q "false" Q " && github.actor != " Q "dependabot[bot]" Q; build = 0; next } { print }')"
expect_shape_problem 'continue-on-error with its value on the next line fails the test' 'is the value of the key above it' \
    "$(insert_after coe-next-line '      - name: Run Unit Tests' '        continue-on-error:\n          true')"
expect_shape_problem 'an if: on the classifier self-test with its value on the next line fails the test' 'has an empty if' \
    "$(insert_after self-test-if-next-line '      - name: Change-classifier self-test' "        if:\n          github.actor != 'dependabot[bot]'")"
# shellcheck disable=SC2016 # an awk program, not shell
expect_shape_problem 'a paths filter inside a one-line trigger' "carries a 'paths-ignore' filter" \
    "$(mutate flow-trigger '$0 == "  pull_request:" { print "  pull_request: {branches: [main], paths-ignore: [docs]}"; drop = 1; next } drop && $0 == "    branches: [\"main\"]" { drop = 0; next } { print }')"

echo
echo '── no other workflow carries the required name ───────────────────────────────────────────'
others=()
while IFS= read -r file; do
    [ "$file" = "$WORKFLOW" ] || others+=("$file")
done < <(find "$REPO_ROOT/.github/workflows" -maxdepth 1 -type f \( -name '*.yml' -o -name '*.yaml' \) | sort)
expect_required_name_lines none "the other ${#others[@]} workflow files never name '$REQUIRED_CHECK' outside a comment" "${others[@]}"
cat > "$TMP_ROOT/two-space-workflow.yml" <<EOF
on:
  pull_request:
jobs:
  shadow:
    name: $REQUIRED_CHECK
    runs-on: ubuntu-24.04
    steps:
      - run: 'true'
EOF
cat > "$TMP_ROOT/four-space-workflow.yml" <<EOF
on:
    pull_request:
jobs:
    shadow:
        name: "$REQUIRED_CHECK"
        runs-on: ubuntu-24.04
        steps:
            - run: 'true'
EOF
expect_required_name_lines found 'a job of that name in another workflow is found'          "$TMP_ROOT/two-space-workflow.yml"
expect_required_name_lines found 'the same job indented by four spaces, name quoted, is found' "$TMP_ROOT/four-space-workflow.yml"

# The main ruleset requires three checks (read 2026-10-07): the job above, `gitleaks`, and
# GitGuardian, which is a GitHub app and runs no workflow. A new required check joins this list.
echo
echo '── the other required check a workflow runs, gitleaks, always starts and does its work ─────'
GITLEAKS_WORKFLOW="$REPO_ROOT/.github/workflows/gitleaks.yml"

# unclassified_problems <workflow> <check name> → start_problems, plus any step-level if: or
# continue-on-error. A required job with no classifier has no reason to skip a step, and either one
# lets it report success without its work: no scan on some PRs, or a found secret that stays green.
unclassified_problems() {
    start_problems "$1" "$2"
    [ -n "$REQ_JOB" ] || return
    local i
    for ((i = 0; i < STEP_COUNT; i++)); do
        if [ -n "${STEP_IF[i]-}" ]; then
            echo "step '${STEP_NAME[i]-#$i}' has an if: — a required job with no classifier must run every step"
        fi
        if [ -n "${STEP_COE[i]-}" ]; then
            echo "step '${STEP_NAME[i]-#$i}' has continue-on-error — its failure would not turn the check red"
        fi
    done
}

# expect_unclassified_problem <description> <expected text> <workflow>
expect_unclassified_problem() {
    local problems
    problems="$(unclassified_problems "$3" gitleaks)"
    if printf '%s' "$problems" | grep -qF -- "$2"; then
        pass "$1"
    else
        fail "$1" "expected a problem containing: $2"$'\n'"got: ${problems:-<none>}"
    fi
}

# gitleaks_job_name_lines <workflow>... → job-level `name: gitleaks` lines. A step of that name has a
# dash before it, and the action's `uses:` line has no `name:`, so neither counts.
gitleaks_job_name_lines() {
    grep -nE "^[[:space:]]+name:[[:space:]]*[\"']?gitleaks[\"']?[[:space:]]*(#.*)?$" "$@" /dev/null || true
}

problems_gitleaks="$(unclassified_problems "$GITLEAKS_WORKFLOW" gitleaks)"
if [ -z "$problems_gitleaks" ]; then
    pass "gitleaks.yml: 'gitleaks' has no needs:, no if:, no continue-on-error and no paths filter"
else
    fail "gitleaks.yml: 'gitleaks' has no needs:, no if:, no continue-on-error and no paths filter" "$problems_gitleaks"
fi
expect_unclassified_problem 'a job-level if: on the gitleaks job is caught' 'job-level if:' \
    "$(insert_after_in "$GITLEAKS_WORKFLOW" gitleaks-job-if '    name: gitleaks' "    if: github.actor != 'dependabot[bot]'")"
expect_unclassified_problem 'an if: on the scan step is caught' "step 'gitleaks' has an if:" \
    "$(insert_after_in "$GITLEAKS_WORKFLOW" gitleaks-step-if '      - name: gitleaks' "        if: github.actor != 'dependabot[bot]'")"
expect_unclassified_problem 'continue-on-error on the scan step is caught' "step 'gitleaks' has continue-on-error" \
    "$(insert_after_in "$GITLEAKS_WORKFLOW" gitleaks-step-coe '      - name: gitleaks' '        continue-on-error: true')"

gitleaks_names="$(gitleaks_job_name_lines "$REPO_ROOT"/.github/workflows/*.y*ml | grep -c .)"
if [ "$gitleaks_names" = 1 ]; then
    pass "exactly one job in all workflows is named 'gitleaks'"
else
    fail "exactly one job in all workflows is named 'gitleaks'" "found $gitleaks_names"
fi
printf 'jobs:\n  shadow:\n    name: gitleaks\n    needs: build\n' > "$TMP_ROOT/gitleaks-shadow.yml"
gitleaks_names="$(gitleaks_job_name_lines "$REPO_ROOT"/.github/workflows/*.y*ml "$TMP_ROOT/gitleaks-shadow.yml" | grep -c .)"
if [ "$gitleaks_names" = 2 ]; then
    pass "a second job named 'gitleaks' in another workflow is counted"
else
    fail "a second job named 'gitleaks' in another workflow is counted" "found $gitleaks_names"
fi

load_required_job "$WORKFLOW"
if [ -n "$problems" ] || [ "$DIFF_IDX" -lt 0 ]; then
    echo
    echo 'the replay needs the real workflow in the right shape — skipped until the cases above pass'
    printf '\n%d/%d cases FAILED\n' "$failures" "$cases"
    exit 1
fi

echo
echo '── the replay tells a skipped gate from a run one ──────────────────────────────────────────'
expect_replay gate-skipped 'healthy classifier, docs-only PR: green with the gate skipped (#285 stays fixed)' healthy 'docs/notes.md'
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
