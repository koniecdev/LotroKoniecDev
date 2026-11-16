#!/usr/bin/env bash
# Offline self-test for build-once in CD (ADR-0070, #1344).
#
# The bug it guards: a second build of a commit never gives the same digest, so building a commit
# that already has an image moves `sha-<short>` away from the bytes that passed staging and that
# prod runs. Two parts:
#
#   1. scripts/ci/commit-image.sh, run with stub `docker` and `gh` first on PATH. `find` may say
#      "build" only when the registry says the tag is missing, and stops on any answer it cannot
#      read. It asks gh for this repo's cd.yml and this run's ref, checked as the exact command
#      line; that real gh refuses another ref was checked live. `name` never moves an existing tag
#      (except `latest`) and reads the sha tag back. The docker stub answers a digest only for the
#      printf format, because some buildx versions print the whole view for a bare one.
#   2. The workflow shape. No workflow starts on a tag, a release or a created ref. In cd.yml's
#      build-and-push job, no step before the lookup writes to the registry, every later step runs
#      only when it found nothing, the build pushes no tag, and naming the image is the last step,
#      after the Trivy scan, cosign signing and the attestation. No step and not the job may go on
#      after an error, and the job has a concurrency group per commit and image, so two runs of one
#      commit never name it at once. A broken copy of the workflows for each check proves that it
#      still fires.
#
# The YAML reader understands the block style the workflows use: two-space indent, one key per
# line. Directly under `on:`, a form it cannot follow (`on:` written inline, a list, an odd indent,
# a `push:` with an inline value) fails the test instead of being guessed at. Below `push:`, it
# reads the four-space keys `branches`, `branches-ignore`, `tags` and `tags-ignore`, quoted or not,
# with or without a space before the colon. A missing cd.yml or an empty workflow folder fails too.
# CI-only (Linux runners), so no .ps1 twin.
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
        if [ -n "${STUB_INSPECT_ERROR_AFTER_CREATE:-}" ] && [ -f "$STUB_STATE/created" ]; then
            printf '%s\n' "$STUB_INSPECT_ERROR_AFTER_CREATE" >&2
            exit 1
        fi
        if [ -n "${STUB_INSPECT_ERROR:-}" ]; then
            printf '%s\n' "$STUB_INSPECT_ERROR" >&2
            exit 1
        fi
        if [ -n "${STUB_INSPECT_STDOUT:-}" ]; then
            printf '%s\n' "$STUB_INSPECT_STDOUT"
            exit 0
        fi
        if [ -f "$(tag_file "$ref")" ]; then
            case "${5-} ${6-}" in
                '--format {{printf "%s" .Manifest.Digest}}')
                    cat "$(tag_file "$ref")"
                    ;;
                '--format {{.Manifest.Digest}}')
                    printf 'Name:      %s\nMediaType: application/vnd.oci.image.index.v1+json\nDigest:    %s\n' \
                        "$ref" "$(cat "$(tag_file "$ref")")"
                    ;;
                *)
                    echo "stub docker: unexpected inspect format: $*" >&2
                    exit 98
                    ;;
            esac
            exit 0
        fi
        printf 'ERROR: %s: not found\n' "$ref" >&2
        exit 1
        ;;
    create)
        if [ -n "${STUB_CREATE_ERROR:-}" ]; then
            printf '%s\n' "$STUB_CREATE_ERROR" >&2
            exit 1
        fi
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
        : > "$STUB_STATE/created"
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
    run_script find IMAGE_REPO="$REPO" IMAGE_TAG='sha-abc1234' GITHUB_REPOSITORY='owner/app' \
        GITHUB_REF='refs/heads/main' "$@"
}

# The one gh call find may make for digest $1 on a run from ref $2.
verify_line() {
    printf 'attestation verify oci://%s@%s --repo owner/app --signer-workflow owner/app/.github/workflows/cd.yml --source-ref %s' \
        "$REPO" "$1" "$2"
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
    && [ "$(cat "$STATE/gh.calls")" = "$(verify_line "$D1" refs/heads/main)" ]; then
    pass 'an image that cd.yml in this repo built from this ref is reused'
else
    fail "expected exit 0, exists=true and exactly '$(verify_line "$D1" refs/heads/main)', got exit $LAST_STATUS, '$(cat "$STATE/out")' and '$(cat "$STATE/gh.calls")'"
fi

CASE='find: a run from a branch'
reset_state
has_tag "$REPO:sha-abc1234" "$D1"
attested "$D1"
run_find GITHUB_REF='refs/heads/1344-some-branch'
if [ "$LAST_STATUS" -eq 0 ] \
    && [ "$(cat "$STATE/gh.calls")" = "$(verify_line "$D1" refs/heads/1344-some-branch)" ]; then
    pass "the check names the run's own ref, not a fixed one"
else
    fail "expected exactly '$(verify_line "$D1" refs/heads/1344-some-branch)', got exit $LAST_STATUS and '$(cat "$STATE/gh.calls")'"
fi

CASE='find: an image without provenance'
reset_state
has_tag "$REPO:sha-abc1234" "$D1"
run_find
if [ "$LAST_STATUS" -ne 0 ] && [ ! -s "$STATE/out" ] \
    && grep -qF 'built it from refs/heads/main' <<< "$LAST_OUTPUT"; then
    pass 'an existing tag that does not verify stops the job, decides nothing and names the ref'
else
    fail "expected a stop with no output that names refs/heads/main, got exit $LAST_STATUS and '$(cat "$STATE/out")'"
fi

CASE='find: no GITHUB_REF'
reset_state
has_tag "$REPO:sha-abc1234" "$D1"
attested "$D1"
run_script find IMAGE_REPO="$REPO" IMAGE_TAG='sha-abc1234' GITHUB_REPOSITORY='owner/app'
if [ "$LAST_STATUS" -ne 0 ] && [ ! -s "$STATE/out" ] && [ ! -s "$STATE/gh.calls" ]; then
    pass 'a run with no ref stops before it asks gh, so the check never loses its ref'
else
    fail "expected a stop with no output and no gh call, got exit $LAST_STATUS and '$(cat "$STATE/gh.calls")'"
fi

# Real gh reads an empty --source-ref as no pin at all.
CASE='find: an empty GITHUB_REF'
reset_state
has_tag "$REPO:sha-abc1234" "$D1"
attested "$D1"
run_find GITHUB_REF=''
if [ "$LAST_STATUS" -ne 0 ] && [ ! -s "$STATE/out" ] && [ ! -s "$STATE/gh.calls" ]; then
    pass 'an empty ref stops before it asks gh, because gh would check no ref at all'
else
    fail "expected a stop with no output and no gh call, got exit $LAST_STATUS and '$(cat "$STATE/gh.calls")'"
fi

for answer in \
    'ERROR: failed to authorize: failed to fetch anonymous token: unexpected status from GET request to https://ghcr.io/token: 403 Forbidden' \
    'ERROR: unexpected status from HEAD request to https://ghcr.io/v2/owner/app/manifests/sha-abc1234: 401 Unauthorized' \
    'ERROR: denied: requested access to the resource is denied' \
    'ERROR: unexpected status from HEAD request to https://ghcr.io/v2/owner/app/manifests/sha-abc1234: 503 Service Unavailable' \
    'ERROR: dial tcp: lookup ghcr.io on 127.0.0.53:53: no such host' \
    'ERROR: context deadline exceeded' \
    'ERROR: failed to copy: httpReadSeeker: failed open: content at https://ghcr.io/v2/owner/app/manifests/sha256:3333333333333333333333333333333333333333333333333333333333333333 not found: not found'; do
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
run_script find IMAGE_REPO="$REPO" GITHUB_REPOSITORY='owner/app' GITHUB_REF='refs/heads/main'
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

# The tag exists, but a child of its image index is gone. That error also ends in ": not found".
CASE='name: a child of the tagged index is gone'
reset_state
has_tag "$REPO:sha-abc1234" "$D2"
run_name TAGS='ghcr.io/owner/app:sha-abc1234' STUB_INSPECT_ERROR='ERROR: failed to copy: httpReadSeeker: failed open: content at https://ghcr.io/v2/owner/app/manifests/sha256:3333333333333333333333333333333333333333333333333333333333333333 not found: not found'
if [ "$LAST_STATUS" -ne 0 ] && ! created && [ "$(tag_names "$REPO:sha-abc1234")" = "$D2" ]; then
    pass 'a missing child manifest is not a missing tag, so no tag is written'
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

CASE='name: the registry refuses the write'
reset_state
run_name TAGS=$'ghcr.io/owner/app:sha-abc1234\nghcr.io/owner/app:latest' \
    STUB_CREATE_ERROR='ERROR: denied: permission_denied: write_package'
if [ "$LAST_STATUS" -ne 0 ] && [ -z "$(tag_names "$REPO:sha-abc1234")" ]; then
    pass 'a failed write stops the job'
else
    fail "expected a stop with no tag written, got exit $LAST_STATUS"
fi

CASE='name: the tag cannot be read back'
reset_state
run_name TAGS='ghcr.io/owner/app:sha-abc1234' STUB_INSPECT_ERROR_AFTER_CREATE='ERROR: context deadline exceeded'
if [ "$LAST_STATUS" -ne 0 ] && grep -qF 'After tagging' <<< "$LAST_OUTPUT"; then
    pass 'a read-back that gets no answer stops the job'
else
    fail "expected a stop at the read-back, got exit $LAST_STATUS"
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
for input in "DIGEST=sha256:1234" "TAGS=ghcr.io/other/app:sha-abc1234" "TAGS="; do
    reset_state
    run_name TAGS='ghcr.io/owner/app:sha-abc1234' "$input"
    if [ "$LAST_STATUS" -ne 0 ] && ! created; then
        pass "refused: $input"
    else
        fail "expected a stop with no retag for $input, got exit $LAST_STATUS"
    fi
done

CASE='name: TAGS holds only empty lines'
reset_state
run_name $'TAGS=\n\n'
if [ "$LAST_STATUS" -ne 0 ] && ! created && grep -qF 'TAGS names no tag' <<< "$LAST_OUTPUT"; then
    pass 'a TAGS value with no tag in it stops the job'
else
    fail "expected a stop with 'TAGS names no tag' and no retag, got exit $LAST_STATUS"
fi

# ── Workflow shape ──────────────────────────────────────────────────────────────────────────────

# Prints why the workflows in $1 can start on a tag, a release or a new ref; prints nothing when
# none can.
tag_trigger_problems() {
    local file count=0
    for file in "$1"/*.yml "$1"/*.yaml; do
        [ -f "$file" ] || continue
        count=$((count + 1))
        # A push trigger with no branch filter runs on every tag too. Each line is read with its
        # quotes and any space before a colon taken out, so `'tags' :` reads as `tags:`.
        awk -v file="${file##*/}" -v q="'" '
            function close_push() {
                if (in_push && !has_branches) print file ": push: has no branch filter, so it runs on tags"
                in_push = 0
            }
            {
                line = $0
                gsub("[\"" q "]", "", line)
                gsub(/[[:space:]]+:/, ":", line)
            }
            line ~ /^on:/ {
                found = 1
                if (line !~ /^on:[[:space:]]*(#.*)?$/) { print file ": on: is written inline"; exit }
                in_on = 1
                next
            }
            in_on && line ~ /^[^[:space:]#]/ { close_push(); in_on = 0 }
            !in_on { next }
            line ~ /^[[:space:]]*(#.*)?$/ { next }
            line ~ /^  [A-Za-z_-]+:/ {
                close_push()
                key = line
                sub(/^  /, "", key)
                sub(/:.*/, "", key)
                seen_key = 1
                if (key == "release" || key == "create") print file ": starts on " key
                if (key == "push") {
                    if (line !~ /^  push:[[:space:]]*(#.*)?$/) { print file ": push: has an inline value"; next }
                    in_push = 1
                    has_branches = 0
                }
                next
            }
            # Under on:, only two-space keys and the deeper lines below them are read. A list, an
            # odd indent or a key before any two-space key is a form this reader cannot follow.
            !seen_key || line !~ /^    / { print file ": on: has a form this test cannot read: " $0; next }
            in_push && line ~ /^    (branches|branches-ignore):/ { has_branches = 1 }
            in_push && line ~ /^    (tags|tags-ignore):/ { print file ": push: lists tags" }
            END {
                close_push()
                if (!found) print file ": has no on: section"
            }
        ' "$file"
    done
    if [ "$count" -eq 0 ]; then
        echo "${1}: no workflow file to read"
    fi
}

# Prints the shape problems it knows in cd.yml ($1): ways to build over an existing image or to name
# an unchecked one.
build_job_problems() {
    if [ ! -r "$1" ]; then
        echo "${1}: cannot read the file"
        return
    fi
    awk '
        /^  build-and-push:/ { in_job = 1; next }
        in_job && /^  [^ #]/ { in_job = 0 }
        !in_job { next }
        /^ *continue-on-error:/ { on_error = 1 }
        /^      group: / { group = $0 }
        /^      - / { n++; text[n] = ""; id[n] = ""; cond[n] = "" }
        n == 0 { next }
        { text[n] = text[n] "\n" $0 }
        /^ *(- )?id: / { v = $0; sub(/^ *(- )?id: */, "", v); id[n] = v }
        /^ *(- )?if: / { v = $0; sub(/^ *(- )?if: */, "", v); cond[n] = v }
        END {
            lookup = 0
            for (i = 1; i <= n; i++) if (id[i] == "existing") lookup = i
            if (on_error) print "the job or a step has continue-on-error"
            if (group !~ /head_sha/ || group !~ /matrix\.image/) print "the job has no concurrency group per commit and image"
            if (!lookup) { print "no lookup step (id: existing)"; exit }
            if (text[lookup] !~ /scripts\/ci\/commit-image\.sh find/) print "the lookup does not run commit-image.sh find"
            build = 0; name = 0; attest = 0; scan = 0; sign = 0
            for (i = 1; i <= n; i++) {
                if (id[i] == "build") build = i
                if (id[i] == "name") name = i
                if (text[i] ~ /actions\/attest-build-provenance@/) attest = i
                if (text[i] ~ /aquasecurity\/trivy-action@/) scan = i
                if (text[i] ~ /\n +run: cosign sign /) sign = i
            }
            for (i = 1; i < lookup; i++)
                if (text[i] ~ /build-push-action@|imagetools create|docker push|buildx build/) print "step " i " writes to the registry before the lookup"
            for (i = lookup + 1; i <= n; i++)
                if (cond[i] != "steps.existing.outputs.exists == '\''false'\''") print "step " i " (" id[i] ") runs without the exists == '\''false'\'' guard"
            for (i = lookup + 1; i <= n; i++)
                if (i != build && i != name && text[i] ~ /build-push-action@|imagetools create|docker push|buildx build/) print "step " i " writes to the registry besides the build and the naming step"
            for (i = 1; i <= n; i++)
                if (text[i] ~ /type=semver/) print "step " i " adds a version tag (type=semver)"
            if (!build) print "no build step (id: build)"
            else {
                if (text[build] !~ /push-by-digest=true/) print "the build step does not push by digest"
                if (text[build] ~ /\n +tags:/) print "the build step lists tags"
                if (text[build] ~ /\n +push: *true/) print "the build step pushes tags (push: true)"
            }
            if (!name) print "no naming step (id: name)"
            else {
                if (text[name] !~ /scripts\/ci\/commit-image\.sh name/) print "the naming step does not run commit-image.sh name"
                if (name != n) print "the naming step is not the last step"
                if (!attest || attest > name) print "the naming step does not come after the attestation"
                if (!scan || scan > name) print "the naming step does not come after the Trivy scan"
                if (!sign || sign > name) print "the naming step does not come after cosign sign"
            }
        }
    ' "$1"
}

CASE='usage: an unknown mode'
for mode in '' build; do
    reset_state
    run_script "$mode" IMAGE_REPO="$REPO"
    if [ "$LAST_STATUS" -ne 0 ] && ! created && [ ! -s "$STATE/gh.calls" ]; then
        pass "refused: mode '${mode}'"
    else
        fail "expected a stop for mode '${mode}', got exit $LAST_STATUS"
    fi
done

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
    pass 'no write before the lookup, every later step guarded, a push by digest, the name last'
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
expect_trigger_problem 'a list under on:'   'a form this test cannot read' '/^on:/a\  - push'
expect_trigger_problem 'a quoted push key'  'push: lists tags'            's/^  push:/  "push":/; /^  "push":/a\    tags: ["v*"]'
expect_trigger_problem 'a four-space indent' 'a form this test cannot read' '/^on:/,/^[a-z]/s/^  /    /'
expect_trigger_problem 'quoted tags'        'push: lists tags'            $'/^  push:/a\\    \'tags\': [\'v*\']'
expect_trigger_problem 'a space before :'   'push: lists tags'            '/^  push:/a\    tags : [v*]'
expect_trigger_problem 'a create trigger'   'starts on create'            '/^on:/a\  create:'
expect_trigger_problem 'an inline push:'    'push: has an inline value'   's/^  push:.*/  push: {branches: [main]}/'
expect_trigger_problem 'no on: section'     'has no on: section'          's/^on:/jobs-before:/'
expect_trigger_problem 'a tags-ignore filter' 'push: lists tags'          '/^  push:/a\    tags-ignore: ["v*"]'

CASE='shape fires: an empty workflow folder'
rm -rf "$TMP_ROOT/empty"
mkdir -p "$TMP_ROOT/empty"
LAST_OUTPUT="$(tag_trigger_problems "$TMP_ROOT/empty")"
if grep -qF 'no workflow file to read' <<< "$LAST_OUTPUT"; then
    pass 'reported: no workflow file to read'
else
    fail "expected 'no workflow file to read'"
fi

CASE='shape fires: no cd.yml'
LAST_OUTPUT="$(build_job_problems "$TMP_ROOT/empty/cd.yml")"
if grep -qF 'cannot read the file' <<< "$LAST_OUTPUT"; then
    pass 'reported: cannot read the file'
else
    fail "expected 'cannot read the file'"
fi

expect_build_problem 'a lost guard'         'runs without the exists'     '0,/^        if: steps.existing.outputs.exists == .false.$/{//d;}'
expect_build_problem 'a tagged push'        'the build step pushes tags (push: true)' 's/push-by-digest=true,//; /^          outputs: /a\          push: true'
expect_build_problem 'a tags input'         'the build step lists tags'   '/^          outputs: /a\          tags: ghcr.io/owner/app:sha-abc1234'
expect_build_problem 'no push by digest'    'the build step does not push by digest' 's/push-by-digest=true,//'
expect_build_problem 'no lookup step'       'no lookup step (id: existing)' 's/^        id: existing$/        id: other/'
expect_build_problem 'a push before lookup' 'writes to the registry before the lookup' '/- name: Look up this commit.s existing image/i\      - name: Early push\n        run: docker push ghcr.io/owner/app:sha-abc1234'
expect_build_problem 'no build step'        'no build step (id: build)'   's/^        id: build$/        id: other/'
expect_build_problem 'no naming step'       'no naming step (id: name)'   's/^        id: name$/        id: other/'
expect_build_problem 'a naming step that is not the script' 'the naming step does not run commit-image.sh name' 's#scripts/ci/commit-image.sh name#true#'
expect_build_problem 'no Trivy scan'        'does not come after the Trivy scan' 's#aquasecurity/trivy-action@#aquasecurity/other-action@#'
expect_build_problem 'no signing'           'does not come after cosign sign' 's/run: cosign sign /run: cosign verify /'
expect_build_problem 'an unguarded naming step' 'runs without the exists' '/^        id: name$/{n;/^        if: /d;}'
expect_build_problem 'a build before lookup' 'writes to the registry before the lookup' '/- name: Look up this commit.s existing image/i\      - name: Early build\n        uses: docker/build-push-action@v7'
expect_build_problem 'a buildx push before lookup' 'writes to the registry before the lookup' '/- name: Look up this commit.s existing image/i\      - name: Early build\n        run: docker buildx build --push .'
expect_build_problem 'a scan that may fail' 'has continue-on-error'      '/^      - name: Scan image for vulnerabilities/a\        continue-on-error: true'
expect_build_problem 'a job that may fail'  'has continue-on-error'      '/^    concurrency:/i\    continue-on-error: true'
expect_build_problem 'no concurrency group' 'no concurrency group per commit and image' '/^      group: cd-build-/d'
expect_build_problem 'a second tagging step' 'writes to the registry besides the build and the naming step' '/^      - name: Scan image for vulnerabilities/i\      - name: Early name\n        if: steps.existing.outputs.exists == '"'"'false'"'"'\n        run: docker buildx imagetools create --tag ghcr.io/owner/app:sha-abc1234 ghcr.io/owner/app@sha256:1'
expect_build_problem 'a semver tag'         'adds a version tag (type=semver)' '/^            type=raw,value=sha-/a\            type=semver,pattern={{version}}'
expect_build_problem 'a step after naming'  'the naming step is not the last step' '/run: \.\/scripts\/ci\/commit-image\.sh name/a\      - name: After\n        if: steps.existing.outputs.exists == '"'"'false'"'"'\n        run: true'
expect_build_problem 'no attestation'       'does not come after the attestation' 's#actions/attest-build-provenance@#actions/attest-other@#'
expect_build_problem 'a lookup that builds' 'the lookup does not run commit-image.sh find' 's#scripts/ci/commit-image.sh find#true#'

echo
if [ "$failures" -ne 0 ]; then
    printf '%d of %d case(s) FAILED.\n' "$failures" "$cases"
    exit 1
fi
printf 'All %d commit-image case(s) passed.\n' "$cases"
