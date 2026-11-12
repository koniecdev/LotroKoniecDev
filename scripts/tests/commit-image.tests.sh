#!/usr/bin/env bash
# Offline self-test for build-once in CD (ADR-0070, #1344).
#
# The bug it guards: a second build of a commit never gives the same digest, so building a commit
# that already has an image moves `sha-<short>` away from the bytes that passed staging and that
# prod runs. Two parts:
#
#   1. scripts/ci/commit-image.sh, run with stub `docker` and `gh` first on PATH. `find` may say
#      "build" only when the registry says the tag is missing, and stops on any answer it cannot
#      read. `name` never moves an existing tag (except `latest`) and reads every tag back.
#   2. The workflow shape. No workflow starts on a tag, a release or a created ref. In cd.yml's
#      build-and-push job, the lookup comes first, every later step runs only when it found nothing,
#      the build pushes no tag, and naming the image is the last step, after the attestation.
#      Broken copies of the workflows prove that each check still fires.
#
# The YAML reader understands the block style the workflows use: two-space indent, one key per
# line. A form it cannot follow (`on:` written inline, a `push:` with an inline value) fails the
# test instead of being guessed at. CI-only (Linux runners), so no .ps1 twin.
set -uo pipefail
export LC_ALL=C

# With CDPATH set, `cd` to a relative path may print the folder, and that text would end up here (#1048).
REPO_ROOT="$(CDPATH='' cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd -P)"
SCRIPT="$REPO_ROOT/scripts/ci/commit-image.sh"
WORKFLOWS="$REPO_ROOT/.github/workflows"

TMP_ROOT="$(mktemp -d)" || exit 1
TMP_ROOT="$(CDPATH='' cd -- "$TMP_ROOT" && pwd -P)" || exit 1
[ -d "$TMP_ROOT" ] || exit 1
trap 'rm -rf "$TMP_ROOT"' EXIT

REPO='ghcr.io/owner/app'
D1="sha256:$(printf '1%.0s' {1..64})"
D2="sha256:$(printf '2%.0s' {1..64})"

cases=0
failures=0
CASE=''
LAST_OUTPUT=''
LAST_STATUS=0

pass() {
    cases=$((cases + 1))
    printf 'PASS  [%s] %s\n' "$CASE" "$1"
}

fail() {
    cases=$((cases + 1))
    failures=$((failures + 1))
    printf 'FAIL  [%s] %s\n' "$CASE" "$1"
    printf '%s\n' "$LAST_OUTPUT" | sed 's/^/        /'
}

# ── Stubs ────────────────────────────────────────────────────────────────────────────────────────
STUB_BIN="$TMP_ROOT/bin"
STATE="$TMP_ROOT/state"
mkdir -p "$STUB_BIN"

cat > "$STUB_BIN/docker" <<'STUB'
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$*" >> "$STUB_STATE/docker.calls"
tag_file() { printf '%s/tags/%s' "$STUB_STATE" "$(printf '%s' "$1" | tr '/:@' '___')"; }
if [ "${1-} ${2-}" != 'buildx imagetools' ]; then
    echo "stub docker: unexpected call: $*" >&2
    exit 98
fi
case "${3-}" in
    inspect)
        ref="$4"
        if [ -n "${STUB_INSPECT_ERROR:-}" ]; then
            printf '%s\n' "$STUB_INSPECT_ERROR" >&2
            exit 1
        fi
        if [ -n "${STUB_INSPECT_STDOUT:-}" ]; then
            printf '%s\n' "$STUB_INSPECT_STDOUT"
            exit 0
        fi
        if [ -f "$(tag_file "$ref")" ]; then
            cat "$(tag_file "$ref")"
            exit 0
        fi
        printf 'ERROR: %s: not found\n' "$ref" >&2
        exit 1
        ;;
    create)
        shift 3
        tags=()
        while [ "$#" -gt 1 ]; do
            case "$1" in
                --tag) tags+=("$2"); shift 2 ;;
                --*) shift ;;
                *) break ;;
            esac
        done
        digest="${STUB_CREATE_DIGEST:-${1##*@}}"
        for tag in "${tags[@]}"; do
            printf '%s\n' "$digest" > "$(tag_file "$tag")"
        done
        # Another commit's run moving `latest` right after this one wrote it.
        if [ -n "${STUB_LATEST_AFTER_CREATE:-}" ]; then
            printf '%s\n' "$STUB_LATEST_AFTER_CREATE" > "$(tag_file "${1%@*}:latest")"
        fi
        ;;
    *)
        echo "stub docker: unexpected call: $*" >&2
        exit 98
        ;;
esac
STUB

cat > "$STUB_BIN/gh" <<'STUB'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$STUB_STATE/gh.calls"
if [ "${1-} ${2-}" != 'attestation verify' ]; then
    echo "stub gh: unexpected call: $*" >&2
    exit 98
fi
digest="${3##*@}"
grep -qxF "$digest" "$STUB_STATE/attested" 2>/dev/null
STUB
chmod +x "$STUB_BIN/docker" "$STUB_BIN/gh"

reset_state() {
    rm -rf "$STATE"
    mkdir -p "$STATE/tags"
    : > "$STATE/attested"
    : > "$STATE/out"
    : > "$STATE/docker.calls"
    : > "$STATE/gh.calls"
}

has_tag() {
    printf '%s\n' "$2" > "$STATE/tags/$(printf '%s' "$1" | tr '/:@' '___')"
}

tag_names() {
    local file
    file="$STATE/tags/$(printf '%s' "$1" | tr '/:@' '___')"
    [ -f "$file" ] && cat "$file"
}

attested() {
    printf '%s\n' "$1" >> "$STATE/attested"
}

# $1 = mode; the rest are VAR=value pairs for the script's environment.
run_script() {
    local mode="$1"
    shift
    LAST_STATUS=0
    LAST_OUTPUT="$(env -i PATH="$STUB_BIN:/usr/bin:/bin" HOME="$TMP_ROOT" STUB_STATE="$STATE" \
        GITHUB_OUTPUT="$STATE/out" "$@" bash "$SCRIPT" "$mode" 2>&1)" || LAST_STATUS=$?
}

run_find() {
    run_script find IMAGE_REPO="$REPO" IMAGE_TAG='sha-abc1234' GITHUB_REPOSITORY='owner/app' "$@"
}

run_name() {
    run_script name IMAGE_REPO="$REPO" DIGEST="$D1" "$@"
}

created() {
    grep -q 'imagetools create' "$STATE/docker.calls"
}

echo '── find: may the job build this commit? ────────────────────────────────────────────────────────'

CASE='find: no image yet'
reset_state
run_find
if [ "$LAST_STATUS" -eq 0 ] && [ "$(cat "$STATE/out")" = 'exists=false' ]; then
    pass 'a tag the registry reports as not found is built'
else
    fail "expected exit 0 and exists=false, got exit $LAST_STATUS and '$(cat "$STATE/out")'"
fi

CASE='find: a checked image'
reset_state
has_tag "$REPO:sha-abc1234" "$D1"
attested "$D1"
run_find
if [ "$LAST_STATUS" -eq 0 ] && [ "$(cat "$STATE/out")" = 'exists=true' ] \
    && grep -qF "attestation verify oci://$REPO@$D1 --repo owner/app" "$STATE/gh.calls"; then
    pass 'an existing image whose provenance verifies by digest is reused'
else
    fail "expected exit 0, exists=true and a verify by digest, got exit $LAST_STATUS and '$(cat "$STATE/out")'"
fi

CASE='find: an image without provenance'
reset_state
has_tag "$REPO:sha-abc1234" "$D1"
run_find
if [ "$LAST_STATUS" -ne 0 ] && [ ! -s "$STATE/out" ]; then
    pass 'an existing tag that does not verify stops the job and decides nothing'
else
    fail "expected a stop with no output, got exit $LAST_STATUS and '$(cat "$STATE/out")'"
fi

for answer in \
    'ERROR: failed to authorize: failed to fetch anonymous token: unexpected status from GET request to https://ghcr.io/token: 403 Forbidden' \
    'ERROR: unexpected status from HEAD request to https://ghcr.io/v2/owner/app/manifests/sha-abc1234: 401 Unauthorized' \
    'ERROR: denied: requested access to the resource is denied' \
    'ERROR: unexpected status from HEAD request to https://ghcr.io/v2/owner/app/manifests/sha-abc1234: 503 Service Unavailable' \
    'ERROR: dial tcp: lookup ghcr.io on 127.0.0.53:53: no such host' \
    'ERROR: context deadline exceeded'; do
    CASE="find: no clear answer (${answer: -24})"
    reset_state
    run_find STUB_INSPECT_ERROR="$answer"
    if [ "$LAST_STATUS" -ne 0 ] && [ ! -s "$STATE/out" ]; then
        pass 'anything but "not found" stops the job instead of building over a tag it cannot see'
    else
        fail "expected a stop with no output, got exit $LAST_STATUS and '$(cat "$STATE/out")'"
    fi
done

CASE='find: inspect prints no digest'
reset_state
run_find STUB_INSPECT_STDOUT=$'Name:      ghcr.io/owner/app:sha-abc1234\nMediaType: application/vnd.oci.image.index.v1+json'
if [ "$LAST_STATUS" -ne 0 ] && [ ! -s "$STATE/out" ]; then
    pass 'output that is not one sha256 digest stops the job'
else
    fail "expected a stop with no output, got exit $LAST_STATUS and '$(cat "$STATE/out")'"
fi

CASE='find: an input is missing'
reset_state
run_script find IMAGE_REPO="$REPO" GITHUB_REPOSITORY='owner/app'
if [ "$LAST_STATUS" -ne 0 ] && [ ! -s "$STATE/out" ]; then
    pass 'a missing IMAGE_TAG stops the job'
else
    fail "expected a stop with no output, got exit $LAST_STATUS"
fi

echo
echo '── name: tag the checked digest, last ─────────────────────────────────────────────────────────'

CASE='name: a new commit on the main tip'
reset_state
run_name TAGS=$'ghcr.io/owner/app:sha-abc1234\nghcr.io/owner/app:latest'
if [ "$LAST_STATUS" -eq 0 ] && [ "$(tag_names "$REPO:sha-abc1234")" = "$D1" ] \
    && [ "$(tag_names "$REPO:latest")" = "$D1" ] \
    && grep -q 'imagetools create --prefer-index=false ' "$STATE/docker.calls"; then
    pass 'both tags name the checked digest, copied with --prefer-index=false'
else
    fail "expected both tags on $D1 and --prefer-index=false, got exit $LAST_STATUS"
fi

CASE='name: latest moves on before the read-back'
reset_state
run_name TAGS=$'ghcr.io/owner/app:sha-abc1234\nghcr.io/owner/app:latest' STUB_LATEST_AFTER_CREATE="$D2"
if [ "$LAST_STATUS" -eq 0 ] && [ "$(tag_names "$REPO:sha-abc1234")" = "$D1" ]; then
    pass 'a newer commit moving latest does not fail a correctly named image'
else
    fail "expected exit 0, got $LAST_STATUS"
fi

CASE='name: the read before tagging is refused'
reset_state
has_tag "$REPO:sha-abc1234" "$D2"
run_name TAGS='ghcr.io/owner/app:sha-abc1234' STUB_INSPECT_ERROR='ERROR: denied: denied'
if [ "$LAST_STATUS" -ne 0 ] && ! created && [ "$(tag_names "$REPO:sha-abc1234")" = "$D2" ]; then
    pass 'a refused read stops the job: a refusal is not "not found", so no tag is written'
else
    fail "expected a stop with no retag, got exit $LAST_STATUS"
fi

CASE='name: the tag already names this digest'
reset_state
has_tag "$REPO:sha-abc1234" "$D1"
run_name TAGS='ghcr.io/owner/app:sha-abc1234'
if [ "$LAST_STATUS" -eq 0 ] && [ "$(tag_names "$REPO:sha-abc1234")" = "$D1" ]; then
    pass 'naming the same digest again is fine'
else
    fail "expected exit 0, got $LAST_STATUS"
fi

CASE='name: the tag names another digest'
reset_state
has_tag "$REPO:sha-abc1234" "$D2"
run_name TAGS=$'ghcr.io/owner/app:sha-abc1234\nghcr.io/owner/app:latest'
if [ "$LAST_STATUS" -ne 0 ] && ! created && [ "$(tag_names "$REPO:sha-abc1234")" = "$D2" ]; then
    pass 'an existing sha tag is never moved, and nothing is tagged'
else
    fail "expected a stop with no retag, got exit $LAST_STATUS"
fi

CASE='name: latest moves'
reset_state
has_tag "$REPO:latest" "$D2"
run_name TAGS=$'ghcr.io/owner/app:sha-abc1234\nghcr.io/owner/app:latest'
if [ "$LAST_STATUS" -eq 0 ] && [ "$(tag_names "$REPO:latest")" = "$D1" ]; then
    pass 'latest follows the newest checked image'
else
    fail "expected latest on $D1, got exit $LAST_STATUS"
fi

CASE='name: the registry does not answer'
reset_state
run_name TAGS='ghcr.io/owner/app:sha-abc1234' STUB_INSPECT_ERROR='ERROR: context deadline exceeded'
if [ "$LAST_STATUS" -ne 0 ] && ! created; then
    pass 'an unreadable answer stops the job before any tag is written'
else
    fail "expected a stop with no retag, got exit $LAST_STATUS"
fi

CASE='name: the retag changes the digest'
reset_state
run_name TAGS='ghcr.io/owner/app:sha-abc1234' STUB_CREATE_DIGEST="$D2"
if [ "$LAST_STATUS" -ne 0 ]; then
    pass 'a tag that does not read back as the signed digest stops the job'
else
    fail 'expected a stop after the read-back'
fi

CASE='name: bad input'
for input in "DIGEST=sha256:1234" "TAGS=ghcr.io/other/app:sha-abc1234" "TAGS=$(printf '\n')"; do
    reset_state
    run_name TAGS='ghcr.io/owner/app:sha-abc1234' "$input"
    if [ "$LAST_STATUS" -ne 0 ] && ! created; then
        pass "refused: $input"
    else
        fail "expected a stop with no retag for $input, got exit $LAST_STATUS"
    fi
done

# ── Workflow shape ──────────────────────────────────────────────────────────────────────────────

# Prints why the workflows in $1 can start on a tag, a release or a new ref; prints nothing when
# none can.
tag_trigger_problems() {
    local file
    for file in "$1"/*.yml "$1"/*.yaml; do
        [ -f "$file" ] || continue
        # A push trigger with no branch filter runs on every tag too.
        awk -v file="${file##*/}" '
            function close_push() {
                if (in_push && !has_branches) print file ": push: has no branch filter, so it runs on tags"
                in_push = 0
            }
            /^"?on"?:/ {
                found = 1
                if ($0 !~ /^"?on"?:[[:space:]]*(#.*)?$/) { print file ": on: is written inline"; exit }
                in_on = 1
                next
            }
            in_on && /^[^[:space:]#]/ { close_push(); in_on = 0 }
            !in_on { next }
            /^  [A-Za-z_-]+:/ {
                close_push()
                key = $1
                sub(/:.*/, "", key)
                if (key == "release" || key == "create") print file ": starts on " key
                if (key == "push") {
                    if ($0 !~ /^  push:[[:space:]]*(#.*)?$/) { print file ": push: has an inline value"; next }
                    in_push = 1
                    has_branches = 0
                }
                next
            }
            in_push && /^    (branches|branches-ignore):/ { has_branches = 1 }
            in_push && /^    (tags|tags-ignore):/ { print file ": push: lists tags" }
            END {
                close_push()
                if (!found) print file ": has no on: section"
            }
        ' "$file"
    done
}

# Prints every way cd.yml ($1) can build over an existing image or name an unchecked one.
build_job_problems() {
    awk '
        /^  build-and-push:/ { in_job = 1; next }
        in_job && /^  [^ #]/ { in_job = 0 }
        !in_job { next }
        /^      - / { n++; text[n] = ""; id[n] = ""; cond[n] = "" }
        n == 0 { next }
        { text[n] = text[n] "\n" $0 }
        /^ *(- )?id: / { v = $0; sub(/^ *(- )?id: */, "", v); id[n] = v }
        /^ *(- )?if: / { v = $0; sub(/^ *(- )?if: */, "", v); cond[n] = v }
        END {
            lookup = 0
            for (i = 1; i <= n; i++) if (id[i] == "existing") lookup = i
            if (!lookup) { print "no lookup step (id: existing)"; exit }
            if (text[lookup] !~ /scripts\/ci\/commit-image\.sh find/) print "the lookup does not run commit-image.sh find"
            build = 0; name = 0; attest = 0
            for (i = 1; i <= n; i++) {
                if (id[i] == "build") build = i
                if (id[i] == "name") name = i
                if (text[i] ~ /actions\/attest-build-provenance@/) attest = i
            }
            for (i = 1; i < lookup; i++)
                if (text[i] ~ /build-push-action@|imagetools create|docker push/) print "step " i " writes to the registry before the lookup"
            for (i = lookup + 1; i <= n; i++)
                if (cond[i] != "steps.existing.outputs.exists == '\''false'\''") print "step " i " (" id[i] ") runs without the exists == '\''false'\'' guard"
            if (!build) print "no build step (id: build)"
            else {
                if (text[build] !~ /push-by-digest=true/) print "the build step does not push by digest"
                if (text[build] ~ /\n +tags:/) print "the build step pushes tags"
                if (text[build] ~ /\n +push: *true/) print "the build step pushes tags (push: true)"
            }
            if (!name) print "no naming step (id: name)"
            else {
                if (text[name] !~ /scripts\/ci\/commit-image\.sh name/) print "the naming step does not run commit-image.sh name"
                if (name != n) print "the naming step is not the last step"
                if (!attest || attest > name) print "the naming step does not come after the attestation"
            }
        }
    ' "$1"
}

echo
echo '── workflow shape ─────────────────────────────────────────────────────────────────────────────'

CASE='shape: the real workflows'
LAST_OUTPUT="$(tag_trigger_problems "$WORKFLOWS")"
if [ -z "$LAST_OUTPUT" ]; then
    pass 'no workflow starts on a tag, a release or a new ref'
else
    fail 'a workflow can start on a tag'
fi
LAST_OUTPUT="$(build_job_problems "$WORKFLOWS/cd.yml")"
if [ -z "$LAST_OUTPUT" ]; then
    pass 'cd.yml looks up first, guards every later step, pushes by digest and names last'
else
    fail 'cd.yml build-and-push has the wrong shape'
fi

# $1 = case, $2 = what the broken copy must report, $3 = a sed program that breaks a copy.
expect_trigger_problem() {
    CASE="shape fires: $1"
    rm -rf "$TMP_ROOT/wf"
    mkdir -p "$TMP_ROOT/wf"
    sed "$3" "$WORKFLOWS/ci.yml" > "$TMP_ROOT/wf/ci.yml"
    LAST_OUTPUT="$(tag_trigger_problems "$TMP_ROOT/wf")"
    if grep -qF "$2" <<< "$LAST_OUTPUT"; then
        pass "reported: $2"
    else
        fail "expected '$2'"
    fi
}

expect_build_problem() {
    CASE="shape fires: $1"
    sed "$3" "$WORKFLOWS/cd.yml" > "$TMP_ROOT/cd.yml"
    if cmp -s "$WORKFLOWS/cd.yml" "$TMP_ROOT/cd.yml"; then
        LAST_OUTPUT='the sed program changed nothing'
        fail 'the broken copy is not broken'
        return
    fi
    LAST_OUTPUT="$(build_job_problems "$TMP_ROOT/cd.yml")"
    if grep -qF "$2" <<< "$LAST_OUTPUT"; then
        pass "reported: $2"
    else
        fail "expected '$2'"
    fi
}

expect_trigger_problem 'a tag filter'       'push: lists tags'            '/^  push:/a\    tags: ["v*"]'
expect_trigger_problem 'a release trigger'  'starts on release'           '/^on:/a\  release:\n    types: [published]'
expect_trigger_problem 'an inline on:'      'on: is written inline'       's/^on:.*/on: [push, pull_request]/'
expect_trigger_problem 'no branch filter'   'push: has no branch filter'  '/^  push:/,/^  [a-z_]*:/{/^    branches:/d;}'

expect_build_problem 'a lost guard'         'runs without the exists'     '0,/^        if: steps.existing.outputs.exists == .false.$/{//d;}'
expect_build_problem 'a tagged push'        'the build step pushes tags'  's/push-by-digest=true,//; /^          outputs: /a\          push: true'
expect_build_problem 'a step after naming'  'the naming step is not the last step' '/run: \.\/scripts\/ci\/commit-image\.sh name/a\      - name: After\n        if: steps.existing.outputs.exists == '"'"'false'"'"'\n        run: true'
expect_build_problem 'no attestation'       'does not come after the attestation' 's#actions/attest-build-provenance@#actions/attest-other@#'
expect_build_problem 'a lookup that builds' 'the lookup does not run commit-image.sh find' 's#scripts/ci/commit-image.sh find#true#'

echo
if [ "$failures" -ne 0 ]; then
    printf '%d of %d case(s) FAILED.\n' "$failures" "$cases"
    exit 1
fi
printf 'All %d commit-image case(s) passed.\n' "$cases"
