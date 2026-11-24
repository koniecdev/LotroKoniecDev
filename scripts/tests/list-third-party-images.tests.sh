#!/usr/bin/env bash
# Offline self-test for scripts/ci/list-third-party-images.sh and the third-party half of
# image-scan.yml (ADR-0076, #1325).
#
# The bugs it guards:
#   1. A server image that drops out of the list is never scanned, and nothing says so. The readers
#      fail loudly on a ref they cannot trust, and the real files must give exactly the nine images
#      the servers run, each pinned by digest.
#   2. A deployed commit that is empty or unknown must not read today's files (an empty one makes
#      `git show :<path>` read the index), and one environment that cannot be read must not stop
#      the legs of the other. So --matrix turns each such case into an error leg.
#   3. An exception that is wider or longer than the owner's rule. Each entry names one package and
#      expires within 30 days (waiting for a vendor fix) or 180 days (a proven false positive, whose
#      purl names the version the proof holds for). The real exception files are checked here on
#      every PR. The date rule only gets easier as days pass, so a date never turns this test red by
#      itself.
#   4. The workflow shape: the third-party legs run when one environment failed to resolve, one red
#      leg stops no other, a leg that could not be listed fails at its first step, a finding fails
#      the leg, the legs give Trivy no registry credentials and scan for vulnerabilities only, and
#      our own images never read an exception file. A broken copy of the workflow proves each check
#      still fires. No root .trivyignore or trivy.yaml may exist, because every scan in a checkout
#      would read it.
# CI-only (Linux runners), so no .ps1 twin.
# shellcheck disable=SC2016 # compose, backup.sh and workflow text holds `${` that must stay literal.
set -uo pipefail
export LC_ALL=C

# With CDPATH set, `cd` to a relative path may print the folder, and that text would end up here (#1048).
REPO_ROOT="$(CDPATH='' cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd -P)"
SCRIPT="$REPO_ROOT/scripts/ci/list-third-party-images.sh"
WORKFLOW="$REPO_ROOT/.github/workflows/image-scan.yml"
TODAY='2026-11-24'

TMP_ROOT="$(mktemp -d)" || exit 1
TMP_ROOT="$(CDPATH='' cd -- "$TMP_ROOT" && pwd -P)" || exit 1
[ -d "$TMP_ROOT" ] || exit 1
trap 'rm -rf "$TMP_ROOT"' EXIT

# Git exports GIT_DIR, GIT_INDEX_FILE and the like to a hook or a `git rebase -x` command. Left in
# place, they would point the scratch repo below at the real one, and its commits would land on
# your branch.
for var in $(git rev-parse --local-env-vars); do
    unset "$var"
done

# Isolate from the developer's git config (gpg signing, hooks, defaults).
export GIT_CONFIG_GLOBAL=/dev/null GIT_CONFIG_SYSTEM=/dev/null
export GIT_AUTHOR_NAME=list-third-party-images-tests GIT_AUTHOR_EMAIL=tests@localhost
export GIT_COMMITTER_NAME=list-third-party-images-tests GIT_COMMITTER_EMAIL=tests@localhost

D1="sha256:$(printf '1%.0s' {1..64})"
D2="sha256:$(printf '2%.0s' {1..64})"

cases=0
failures=0
CASE=''
LAST_OUTPUT=''
LAST_STDOUT=''
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

# Runs the script with the given arguments from the current folder. LAST_STDOUT holds stdout only,
# LAST_OUTPUT stdout and stderr.
run_script() {
    LAST_STATUS=0
    LAST_STDOUT="$(bash "$SCRIPT" "$@" 2> "$TMP_ROOT/stderr")" || LAST_STATUS=$?
    LAST_OUTPUT="${LAST_STDOUT}"$'\n'"$(< "$TMP_ROOT/stderr")"
}

# expect_exit <status> <description> <args…>
expect_exit() {
    local expected="$1" description="$2"
    shift 2
    run_script "$@"
    if [ "$LAST_STATUS" -eq "$expected" ]; then
        pass "$description"
    else
        fail "$description (exit ${LAST_STATUS}, expected ${expected})"
    fi
}

# expect_output <expected stdout> <description> <args…>: exit 0 and exactly that stdout.
expect_output() {
    local expected="$1" description="$2"
    shift 2
    run_script "$@"
    if [ "$LAST_STATUS" -eq 0 ] && [ "$LAST_STDOUT" = "$expected" ]; then
        pass "$description"
    else
        fail "$description (exit ${LAST_STATUS}; expected stdout: ${expected//$'\n'/ | })"
    fi
}

# ── 1. The compose reader ─────────────────────────────────────────────────────────────────────
CASE='compose'
COMPOSE="$TMP_ROOT/compose.yaml"
cat > "$COMPOSE" <<EOF
# The Caddy pin below. A comment that says image: caddy:2.0.0 is not a ref.
x-caddy-image: &caddy-image
  image: caddy:2.11.9-alpine@${D1}

services:
  caddy:
    <<: *caddy-image
  api:
    image: ghcr.io/\${IMAGE_NAMESPACE:-koniecdev}/lotrokoniecdev-tms-api:\${IMAGE_TAG:-latest}
  rabbitmq:
    # image: rabbitmq:3.0.0
    image: rabbitmq:4.3.8-management-alpine
EOF
expect_output "caddy:2.11.9-alpine@${D1}"$'\n'"rabbitmq:4.3.8-management-alpine" \
    'reads indented image lines once, skips comments, the anchor key and our own images' --compose "$COMPOSE"

write_compose_with() {
    printf 'services:\n  one:\n    image: alpine:3.22\n  two:\n    image: %s\n' "$1" > "$TMP_ROOT/bad.yaml"
}
write_compose_with '${REGISTRY:-docker.io}/caddy:2'
expect_exit 2 'another variable in a ref stops the list' --compose "$TMP_ROOT/bad.yaml"
write_compose_with '"caddy:2.11.9-alpine"'
expect_exit 2 'a quoted ref stops the list' --compose "$TMP_ROOT/bad.yaml"
write_compose_with 'caddy:2.11.9-alpine # the proxy'
expect_exit 2 'a trailing comment stops the list' --compose "$TMP_ROOT/bad.yaml"
write_compose_with 'caddy'
expect_exit 2 'a ref with no tag stops the list' --compose "$TMP_ROOT/bad.yaml"
write_compose_with "caddy@${D1}"
expect_exit 2 'a ref with a digest and no tag stops the list' --compose "$TMP_ROOT/bad.yaml"
printf 'services:\n  one:\n    image: alpine:3.22\n  two:\n    - image: caddy:2.11.9-alpine\n' > "$TMP_ROOT/bad.yaml"
expect_exit 2 'an image key as a list item stops the list' --compose "$TMP_ROOT/bad.yaml"
printf 'services:\n  one:\n    image: alpine:3.22\n  two: {image: caddy:2.11.9-alpine}\n' > "$TMP_ROOT/bad.yaml"
expect_exit 2 'an image key in a flow mapping stops the list' --compose "$TMP_ROOT/bad.yaml"
printf 'services:\n  one:\n    image: alpine:3.22\n  two:\n    "image": caddy:2.11.9-alpine\n' > "$TMP_ROOT/bad.yaml"
expect_exit 2 'a quoted image key stops the list' --compose "$TMP_ROOT/bad.yaml"
printf 'services:\n  one:\n    image: alpine:3.22\n  two:\n    image : caddy:2.11.9-alpine\n' > "$TMP_ROOT/bad.yaml"
expect_exit 2 'an image key with a space before the colon stops the list' --compose "$TMP_ROOT/bad.yaml"
expect_exit 2 'a missing file stops the list' --compose "$TMP_ROOT/missing.yaml"
printf 'services:\n  api:\n    image: ghcr.io/${IMAGE_NAMESPACE:-koniecdev}/x:${IMAGE_TAG:-latest}\n' > "$TMP_ROOT/none.yaml"
expect_exit 2 'a file with no third-party image stops the list' --compose "$TMP_ROOT/none.yaml"
expect_exit 2 'one bad file among good ones stops the list' --compose "$COMPOSE" "$TMP_ROOT/none.yaml"
expect_exit 2 '--compose with no file' --compose

# ── 2. The backup reader ──────────────────────────────────────────────────────────────────────
CASE='backup'
BACKUP="$TMP_ROOT/backup.sh"
write_backup() {
    printf '#!/usr/bin/env bash\nSTACK_DIR="${STACK_DIR:-/opt/lotro}"\n%s\nDATABASE_KEYS="a b"\n' "$1" > "$BACKUP"
}
write_backup "POSTGRES_IMAGE=\"\${POSTGRES_IMAGE:-postgres:18.7-alpine@${D2}}\""
expect_output "postgres:18.7-alpine@${D2}" 'reads the default of POSTGRES_IMAGE' --backup "$BACKUP"
write_backup 'POSTGRES_ARGS="--clean"'
expect_exit 2 'no POSTGRES_IMAGE line stops the list' --backup "$BACKUP"
write_backup "POSTGRES_IMAGE=\"\${POSTGRES_IMAGE:-postgres:18}\""$'\n'"POSTGRES_IMAGE=\"\${POSTGRES_IMAGE:-postgres:17}\""
expect_exit 2 'two POSTGRES_IMAGE lines stop the list' --backup "$BACKUP"
write_backup 'POSTGRES_IMAGE="${POSTGRES_IMAGE:-postgres}"'
expect_exit 2 'a default with no tag stops the list' --backup "$BACKUP"
write_backup 'POSTGRES_IMAGE=postgres:18.7-alpine'
expect_exit 2 'a line in another form stops the list' --backup "$BACKUP"
expect_exit 2 'a missing file stops the list' --backup "$TMP_ROOT/missing.sh"
expect_exit 2 '--backup takes exactly one file' --backup "$BACKUP" "$BACKUP"

# ── 3. --matrix in a throwaway repo ───────────────────────────────────────────────────────────
CASE='matrix'
MATRIX_REPO="$TMP_ROOT/repo"
git_in_repo() {
    git -C "$MATRIX_REPO" "$@"
}
write_stack() {
    mkdir -p "$MATRIX_REPO/scripts/hetzner"
    printf 'x-caddy-image: &caddy-image\n  image: caddy:2.11.9-alpine@%s\nservices:\n  rabbitmq:\n    image: %s\n' \
        "$D1" "$1" > "$MATRIX_REPO/compose.hetzner.yaml"
    printf 'POSTGRES_IMAGE="${POSTGRES_IMAGE:-postgres:18.7-alpine@%s}"\n' "$D2" > "$MATRIX_REPO/scripts/hetzner/backup.sh"
}
mkdir -p "$MATRIX_REPO/.github/trivy"
git init -q -b main "$MATRIX_REPO"
write_stack 'rabbitmq:4.3.4-management-alpine'
printf 'services:\n  loki:\n    image: grafana/loki:3.7.10@%s\n' "$D1" > "$MATRIX_REPO/compose.observability.yaml"
printf '{"vulnerabilities":[]}\n' > "$MATRIX_REPO/.github/trivy/caddy.trivyignore.yaml"
printf '{"vulnerabilities":[]}\n' > "$MATRIX_REPO/.github/trivy/grafana-loki.trivyignore.yaml"
git_in_repo add -A
git_in_repo commit -qm 'old'
OLD="$(git_in_repo rev-parse HEAD)"
write_stack "rabbitmq:4.3.8-management-alpine@${D2}"
git_in_repo commit -qam 'new'
NEW="$(git_in_repo rev-parse HEAD)"
git_in_repo rm -q scripts/hetzner/backup.sh
git_in_repo commit -qm 'no backup'
NO_BACKUP="$(git_in_repo rev-parse HEAD)"
git_in_repo checkout -q "$NEW"

# expect_matrix <description> <jq test on the output> <args…>: exit 0, valid JSON, never empty, and
# the jq test holds.
expect_matrix() {
    local description="$1" test="$2"
    shift 2
    run_script --matrix "$@"
    if [ "$LAST_STATUS" -eq 0 ] \
        && jq -e '.include | type == "array" and length > 0' <<< "$LAST_STDOUT" > /dev/null 2>&1 \
        && jq -e "$test" <<< "$LAST_STDOUT" > /dev/null 2>&1; then
        pass "$description"
    else
        fail "$description (exit ${LAST_STATUS})"
    fi
}

cd -- "$MATRIX_REPO" || exit 1
expect_matrix 'the same pin in both environments is one leg named after both' \
    '[.include[] | select(.ref == "caddy:2.11.9-alpine@'"$D1"'")] | length == 1 and .[0].leg == "caddy:2.11.9-alpine@111111111111 (staging, production)" and .[0].error == ""' \
    "staging=$NEW" "production=$OLD"
expect_matrix 'different pins are separate legs' \
    '([.include[] | select(.ref == "rabbitmq:4.3.4-management-alpine")][0].leg == "rabbitmq:4.3.4-management-alpine (production)")
     and ([.include[] | select(.ref == "rabbitmq:4.3.8-management-alpine@'"$D2"'")][0].leg == "rabbitmq:4.3.8-management-alpine@222222222222 (staging)")' \
    "staging=$NEW" "production=$OLD"
expect_matrix 'an image with an exception file gets it, one without gets none' \
    '([.include[] | select(.ref | startswith("caddy:"))][0].ignore == ".github/trivy/caddy.trivyignore.yaml")
     and ([.include[] | select(.ref | startswith("grafana/loki:"))][0].ignore == ".github/trivy/grafana-loki.trivyignore.yaml")
     and ([.include[] | select(.ref | startswith("postgres:"))][0].ignore == "")' \
    "staging=$NEW" "production=$OLD"
expect_matrix 'the observability file is read from the working tree' \
    '[.include[] | select(.leg == "grafana/loki:3.7.10@111111111111 (observability)")] | length == 1' \
    "staging=$NEW" "production=$OLD"
expect_matrix 'the legs are sorted by name' '[.include[].leg] == ([.include[].leg] | sort)' "staging=$NEW" "production=$OLD"
expect_matrix 'an empty commit is an error leg, and the other environment is still listed' \
    '([.include[] | select(.leg == "staging: no deployed commit")] | length == 1 and (.[0].error | length > 0) and .[0].ref == "")
     and ([.include[] | select(.ref | startswith("rabbitmq:4.3.4"))][0].leg == "rabbitmq:4.3.4-management-alpine (production)")' \
    "staging=" "production=$OLD"
expect_matrix 'a short commit is an error leg' \
    '[.include[] | select(.leg == "production: no deployed commit")] | length == 1' "staging=$NEW" "production=${OLD:0:7}"
expect_matrix 'an upper-case commit is an error leg' \
    '[.include[] | select(.leg == "production: no deployed commit")] | length == 1' \
    "staging=$NEW" "production=$(tr 'a-f' 'A-F' <<< "$OLD")"
expect_matrix 'a commit this checkout does not have is an error leg' \
    '[.include[] | select(.leg == "production: unknown commit")] | length == 1' \
    "staging=$NEW" "production=$(printf 'f%.0s' {1..40})"
expect_matrix 'a commit without backup.sh is an error leg, and its compose images stay' \
    '([.include[] | select(.leg == "production: cannot list scripts/hetzner/backup.sh")] | length == 1)
     and ([.include[] | select(.ref | startswith("rabbitmq:4.3.8"))][0].leg | test("staging, production"))' \
    "staging=$NEW" "production=$NO_BACKUP"
mv compose.observability.yaml compose.observability.yaml.away
expect_matrix 'a missing observability file is an error leg, and the environments are still listed' \
    '([.include[] | select(.leg == "observability: cannot list compose.observability.yaml")] | length == 1)
     and ([.include[] | select(.ref | startswith("caddy:"))] | length == 1)' \
    "staging=$NEW" "production=$OLD"
mv compose.observability.yaml.away compose.observability.yaml
expect_exit 2 'a missing environment is a wrong call' --matrix "staging=$NEW"
expect_exit 2 'an unknown environment is a wrong call' --matrix "staging=$NEW" "production=$OLD" "qa=$OLD"
expect_exit 2 'an environment given twice is a wrong call' --matrix "staging=$NEW" "staging=$OLD" "production=$OLD"
expect_exit 2 'an argument without = is a wrong call' --matrix "staging" "production=$OLD"
cd -- "$TMP_ROOT" || exit 1
expect_exit 2 'outside a repo is a wrong call' --matrix "staging=$NEW" "production=$OLD"
cd -- "$REPO_ROOT" || exit 1

# ── 4. --check-exceptions ─────────────────────────────────────────────────────────────────────
CASE='exceptions'
EXCEPTIONS="$TMP_ROOT/exceptions.trivyignore.yaml"
VENDOR='Waiting for a vendor fix: the newest release still has it.'
FALSE='False positive: the advisory range misses the backport.'

# entry [jq assignments…]: a valid entry, changed by the assignments.
entry() {
    jq -nc --arg vendor "$VENDOR" "{id: \"CVE-2026-78661\", purls: [\"pkg:golang/golang.org/x/net\"], paths: [\"usr/bin/caddy\"], expired_at: \"2026-12-24T00:00:00Z\", statement: \$vendor} ${1:+| $1}"
}

write_exceptions() {
    printf '{"vulnerabilities": [%s]}\n' "$(IFS=,; printf '%s' "$*")" > "$EXCEPTIONS"
}

# expect_exceptions <status> <description> <entries…>. A refusal must come from a rule, never from
# the check itself breaking on the input.
expect_exceptions() {
    local expected="$1" description="$2"
    shift 2
    write_exceptions "$@"
    run_script --check-exceptions --today "$TODAY" "$EXCEPTIONS"
    if [ "$LAST_STATUS" -eq "$expected" ] && ! grep -qF 'the check could not read the file' <<< "$LAST_OUTPUT"; then
        pass "$description"
    else
        fail "$description (exit ${LAST_STATUS}, expected ${expected} from a rule)"
    fi
}

expect_exceptions 0 'a vendor-fix entry 30 days ahead passes' "$(entry)"
expect_exceptions 0 'a false positive 180 days ahead passes' \
    "$(entry ".statement = \"$FALSE\" | .expired_at = \"2027-05-23T00:00:00Z\" | .purls = [\"pkg:golang/golang.org/x/net@v0.60.0\"]")"
expect_exceptions 1 'a false positive with no package version fails' \
    "$(entry ".statement = \"$FALSE\" | .expired_at = \"2027-05-23T00:00:00Z\"")"
expect_exceptions 0 'an entry with a version in its purl and no paths passes' \
    "$(entry '.purls = ["pkg:apk/alpine/libxml2@2.13.9-r2"] | del(.paths)')"
expect_exceptions 0 'an entry that has already expired passes (the daily scan goes red instead)' \
    "$(entry '.expired_at = "2026-01-01T00:00:00Z"')"
expect_exceptions 0 'the same id for two packages passes' \
    "$(entry)" "$(entry '.purls = ["pkg:golang/stdlib"]')"
expect_exceptions 1 'a vendor-fix entry 31 days ahead fails' "$(entry '.expired_at = "2026-12-25T00:00:00Z"')"
expect_exceptions 1 'a false positive 181 days ahead fails' \
    "$(entry ".statement = \"$FALSE\" | .expired_at = \"2027-05-24T00:00:00Z\" | .purls = [\"pkg:golang/golang.org/x/net@v0.60.0\"]")"
expect_exceptions 1 'a date with no time fails (Trivy stops on it)' "$(entry '.expired_at = "2026-12-24"')"
expect_exceptions 1 'a time other than midnight fails' "$(entry '.expired_at = "2026-12-24T12:00:00Z"')"
expect_exceptions 1 'a date that does not exist fails' "$(entry '.expired_at = "2026-02-30T00:00:00Z"')"
expect_exceptions 1 'a misspelt key fails (Trivy drops it without a word)' \
    "$(entry '.expires_at = .expired_at | del(.expired_at)')"
expect_exceptions 1 'an unknown key next to every required one fails' "$(entry '.note = "kept for later"')"
expect_exceptions 1 'a missing statement fails' "$(entry 'del(.statement)')"
expect_exceptions 1 'a statement with another prefix fails' "$(entry '.statement = "Accepted risk: no reason."')"
expect_exceptions 1 'a prefix with no reason after it fails' "$(entry '.statement = "False positive:  "')"
expect_exceptions 1 'an entry with no purl fails' "$(entry '.purls = []')"
expect_exceptions 1 'an entry with two purls fails' "$(entry '.purls = ["pkg:golang/stdlib", "pkg:golang/golang.org/x/net"]')"
expect_exceptions 1 'a purl with qualifiers fails' "$(entry '.purls = ["pkg:apk/alpine/libxml2@2.13.9-r2?arch=x86_64"]')"
expect_exceptions 1 'a purl with a subpath fails' "$(entry '.purls = ["pkg:golang/golang.org/x/net#http2"]')"
expect_exceptions 1 'an empty paths list fails' "$(entry '.paths = []')"
expect_exceptions 1 'a path with a wildcard fails' "$(entry '.paths = ["usr/bin/*"]')"
expect_exceptions 1 'a path with a leading slash fails' "$(entry '.paths = ["/usr/bin/caddy"]')"
expect_exceptions 1 'an id that is not a CVE or GHSA id fails' "$(entry '.id = "GO-2026-4341"')"
expect_exceptions 1 'the same id and package twice fails' "$(entry)" "$(entry)"
expect_exceptions 1 'an entry that is not an object fails' '"CVE-2026-78661"'
expect_exceptions 1 'an empty list fails'
printf '{"vulnerabilities": [%s], "secrets": []}\n' "$(entry)" > "$EXCEPTIONS"
expect_exit 1 'another top-level key fails' --check-exceptions --today "$TODAY" "$EXCEPTIONS"
printf 'vulnerabilities:\n  - id: CVE-2026-78661\n    purls:\n      - pkg:golang/golang.org/x/net\n    expired_at: 2026-12-24T00:00:00Z\n    statement: "%s"\n' \
    "$VENDOR" > "$EXCEPTIONS"
expect_exit 1 'the plain YAML form fails' --check-exceptions --today "$TODAY" "$EXCEPTIONS"
write_exceptions "$(entry)"
expect_exit 2 'a missing file is a wrong call' --check-exceptions --today "$TODAY" "$EXCEPTIONS" "$TMP_ROOT/missing.yaml"
expect_exit 2 'no file is a wrong call' --check-exceptions --today "$TODAY"
expect_exit 2 'a date that does not exist is a wrong call' --check-exceptions --today '2026-13-01' "$EXCEPTIONS"
expect_exit 2 'a date in another form is a wrong call' --check-exceptions --today '24.11.2026' "$EXCEPTIONS"
expect_exit 2 'an unknown mode is a wrong call' --list

# ── 5. The real files ─────────────────────────────────────────────────────────────────────────
CASE='repo'
cd -- "$REPO_ROOT" || exit 1
EXPECTED_REPOSITORIES='alpine
caddy
grafana/alloy
grafana/grafana
grafana/loki
grafana/tempo
postgres
prom/prometheus
rabbitmq'
run_script --compose compose.hetzner.yaml compose.observability.yaml
compose_refs="$LAST_STDOUT"
compose_status="$LAST_STATUS"
run_script --backup scripts/hetzner/backup.sh
backup_status="$LAST_STATUS"
SERVER_REFS="${compose_refs}"$'\n'"${LAST_STDOUT}"
LAST_OUTPUT="$SERVER_REFS"
repositories="$(sed -E 's/@.*$//; s/:[^:]*$//' <<< "$SERVER_REFS" | sort -u)"
if [ "$compose_status" -eq 0 ] && [ "$backup_status" -eq 0 ] && [ "$repositories" = "$EXPECTED_REPOSITORIES" ] \
    && [ "$(wc -l <<< "$SERVER_REFS")" -eq 9 ]; then
    pass 'the servers run exactly the nine expected third-party images'
else
    fail 'the servers run exactly the nine expected third-party images (a new one joins this list and ADR-0076)'
fi
if ! grep -qvE '@sha256:[0-9a-f]{64}$' <<< "$SERVER_REFS"; then
    pass 'every third-party server image is pinned by digest'
else
    fail 'every third-party server image is pinned by digest'
fi

exception_files=()
for file in .github/trivy/*.trivyignore.yaml; do
    [ -e "$file" ] && exception_files+=("$file")
done
unknown=''
for file in "${exception_files[@]}"; do
    name="$(basename "$file" .trivyignore.yaml)"
    grep -qxF -- "$name" <<< "$(tr '/' '-' <<< "$EXPECTED_REPOSITORIES")" || unknown="${unknown} ${file}"
done
LAST_OUTPUT="files with no image:${unknown}"
if [ -z "$unknown" ]; then
    pass 'every exception file belongs to a server image'
else
    fail 'every exception file belongs to a server image'
fi
if [ "${#exception_files[@]}" -eq 0 ]; then
    LAST_OUTPUT=''
    pass 'no exception file to check'
else
    expect_exit 0 'the exception files keep the rules today' --check-exceptions "${exception_files[@]}"
fi

# ── 6. The workflow shape ─────────────────────────────────────────────────────────────────────
CASE='workflow'

# job_block <file> <job>: the lines of one job, from its key to the next job key.
job_block() {
    awk -v job="  $2:" '
        $0 == job { inside = 1; print; next }
        inside && /^  [^ #]/ { exit }
        inside { print }
    ' "$1"
}

# has_line <text> <exact line>: the text holds that line.
has_line() {
    grep -qxF -- "$2" <<< "$1"
}

# workflow_problems <file>: one line per broken rule, nothing when the shape holds.
workflow_problems() {
    local file="$1" resolve own list scan first_step
    resolve="$(job_block "$file" resolve)"
    own="$(job_block "$file" scan)"
    list="$(job_block "$file" third-party-list)"
    scan="$(job_block "$file" third-party-scan)"
    [ -n "$own" ] || echo 'no scan job'
    [ -n "$list" ] || echo 'no third-party-list job'
    [ -n "$scan" ] || echo 'no third-party-scan job'

    # The deployed commits reach the list job, even when one environment failed to resolve.
    has_line "$resolve" '            echo "${environment}_commit=${sha}" >> "$GITHUB_OUTPUT"' \
        || echo 'resolve does not write the deployed commits'
    has_line "$resolve" '      staging_commit: ${{ steps.tags.outputs.staging_commit }}' \
        && has_line "$resolve" '      production_commit: ${{ steps.tags.outputs.production_commit }}' \
        || echo 'resolve does not hand on the deployed commits'
    has_line "$list" "    if: \${{ !cancelled() && github.repository == 'koniecdev/LotroKoniecDev' }}" \
        || echo 'the list job does not run when resolve failed'
    has_line "$list" '          STAGING_COMMIT: ${{ needs.resolve.outputs.staging_commit }}' \
        && has_line "$list" '          PRODUCTION_COMMIT: ${{ needs.resolve.outputs.production_commit }}' \
        || echo 'the list job does not read the deployed commits'
    grep -qF './scripts/ci/list-third-party-images.sh --matrix' <<< "$list" || echo 'the list job does not run --matrix'

    # One leg per image: it runs when resolve failed, it fails on a finding, and it never stops another.
    has_line "$scan" "    if: \${{ !cancelled() && needs.third-party-list.result == 'success' && github.repository == 'koniecdev/LotroKoniecDev' }}" \
        || echo 'the third-party legs do not run when resolve failed'
    has_line "$scan" '      fail-fast: false' || echo 'one red third-party leg cancels the others'
    first_step="$(awk '/^    steps:$/ { steps = 1; next } steps && /^      - / { print; exit }' <<< "$scan")"
    [ "$first_step" = '      - name: Check that the image could be listed' ] \
        && has_line "$scan" '          LEG_ERROR: ${{ matrix.error }}' \
        && has_line "$scan" '            exit 1' \
        || echo 'an error leg does not fail at its first step'
    has_line "$scan" "          exit-code: '1'" || echo 'a finding does not fail the leg'
    has_line "$scan" '          severity: HIGH,CRITICAL' || echo 'the leg does not scan for HIGH and CRITICAL'
    has_line "$scan" '          scanners: vuln' || echo 'the third-party scan does not set scanners: vuln'
    has_line "$scan" '          trivyignores: ${{ matrix.ignore }}' || echo 'the third-party scan does not read the leg'"'"'s exception file'
    if grep -qE 'TRIVY_USERNAME|TRIVY_PASSWORD|secrets\.' <<< "${list}"$'\n'"${scan}"; then
        echo 'a third-party job passes registry credentials'
    fi
    if grep -qE 'trivyignores|actions/checkout' <<< "$own"; then
        echo 'our own images may read an exception file'
    fi
}

expect_workflow() {
    local expected="$1" description="$2" file="$3" problems
    problems="$(workflow_problems "$file")"
    LAST_OUTPUT="$problems"
    if [ -z "$expected" ] && [ -z "$problems" ]; then
        pass "$description"
    elif [ -n "$expected" ] && grep -qF -- "$expected" <<< "$problems"; then
        pass "$description"
    else
        fail "$description (expected: ${expected:-no problem})"
    fi
}

# expect_broken <description> <expected problem> <sed script>
expect_broken() {
    sed -e "$3" "$WORKFLOW" > "$TMP_ROOT/broken.yml"
    if cmp -s "$WORKFLOW" "$TMP_ROOT/broken.yml"; then
        LAST_OUTPUT='the sed script changed nothing'
        fail "$1"
        return
    fi
    expect_workflow "$2" "$1" "$TMP_ROOT/broken.yml"
}

# expect_changed <description> <expected problem> <job> <exact line> <new text, or - to delete it>
# Changes the first copy of that line inside that job of a copy of the workflow. `\n` in the new
# text starts a new line.
expect_changed() {
    local description="$1" expected="$2" job="$3" old="$4" new="$5"
    awk -v job="  ${job}:" -v old="$old" -v new="$new" '
        $0 == job { inside = 1; print; next }
        inside && /^  [^ #]/ { inside = 0 }
        inside && !done && $0 == old { done = 1; if (new != "-") print new; next }
        { print }
    ' "$WORKFLOW" > "$TMP_ROOT/broken.yml"
    if cmp -s "$WORKFLOW" "$TMP_ROOT/broken.yml"; then
        LAST_OUTPUT="the line was not found in job ${job}: ${old}"
        fail "$description"
        return
    fi
    expect_workflow "$expected" "$description" "$TMP_ROOT/broken.yml"
}

expect_workflow '' 'image-scan.yml keeps the third-party rules' "$WORKFLOW"
expect_changed 'a token given to a third-party scan' 'passes registry credentials' third-party-scan \
    '          trivyignores: ${{ matrix.ignore }}' \
    '          trivyignores: ${{ matrix.ignore }}\n        env:\n          TRIVY_PASSWORD: ${{ secrets.GITHUB_TOKEN }}'
expect_changed 'a third-party scan with the secret scanner' 'does not set scanners: vuln' third-party-scan \
    '          scanners: vuln' -
expect_changed 'a third-party scan without its exception file' 'exception file' third-party-scan \
    '          trivyignores: ${{ matrix.ignore }}' -
expect_changed 'our own images reading an exception file' 'our own images may read an exception file' scan \
    "          exit-code: '1'" "          exit-code: '1'\n          trivyignores: .github/trivy/caddy.trivyignore.yaml"
expect_changed 'a third-party leg that does not fail on a finding' 'a finding does not fail the leg' third-party-scan \
    "          exit-code: '1'" "          exit-code: '0'"
expect_changed 'a third-party leg that scans for CRITICAL only' 'HIGH and CRITICAL' third-party-scan \
    '          severity: HIGH,CRITICAL' '          severity: CRITICAL'
expect_changed 'one red leg cancels the others' 'cancels the others' third-party-scan \
    '      fail-fast: false' '      fail-fast: true'
expect_changed 'the legs follow the default success(), which counts resolve' 'do not run when resolve failed' third-party-scan \
    "    if: \${{ !cancelled() && needs.third-party-list.result == 'success' && github.repository == 'koniecdev/LotroKoniecDev' }}" \
    "    if: github.repository == 'koniecdev/LotroKoniecDev'"
expect_changed 'the list job is skipped when resolve failed' 'list job does not run when resolve failed' third-party-list \
    "    if: \${{ !cancelled() && github.repository == 'koniecdev/LotroKoniecDev' }}" \
    "    if: github.repository == 'koniecdev/LotroKoniecDev'"
expect_changed 'an error leg that goes on to the scan' 'error leg does not fail at its first step' third-party-scan \
    '          LEG_ERROR: ${{ matrix.error }}' -
expect_changed 'resolve stops handing on a commit' 'resolve does not hand on the deployed commits' resolve \
    '      staging_commit: ${{ steps.tags.outputs.staging_commit }}' -
expect_changed 'resolve writes no commit' 'resolve does not write the deployed commits' resolve \
    '            echo "${environment}_commit=${sha}" >> "$GITHUB_OUTPUT"' -
expect_changed 'the list job reads the tag instead of the commit' 'does not read the deployed commits' third-party-list \
    '          STAGING_COMMIT: ${{ needs.resolve.outputs.staging_commit }}' \
    '          STAGING_COMMIT: ${{ needs.resolve.outputs.staging }}'
expect_broken 'no third-party scan job' 'no third-party-scan job' 's/^  third-party-scan:$/  other-scan:/'

# Trivy reads a `.trivyignore` and its `trivy.yaml` settings from the folder it runs in. A third-party
# leg checks the repo out, and so do the scans of our own images in cd.yml and pr-verify, so such a
# file at the root would hide findings in all of them, outside any rule above.
CASE='root-ignore'
root_ignore_files() {
    local name
    for name in .trivyignore .trivyignore.yaml .trivyignore.yml trivy.yaml trivy.yml; do
        [ -e "$1/$name" ] && printf '%s\n' "$name"
    done
    return 0
}
LAST_OUTPUT="$(root_ignore_files "$REPO_ROOT")"
if [ -z "$LAST_OUTPUT" ]; then
    pass 'the repo has no root .trivyignore or trivy.yaml'
else
    fail 'the repo has no root .trivyignore or trivy.yaml'
fi
mkdir -p "$TMP_ROOT/fake-root"
printf 'severity: CRITICAL\n' > "$TMP_ROOT/fake-root/trivy.yaml"
LAST_OUTPUT="$(root_ignore_files "$TMP_ROOT/fake-root")"
if grep -qxF 'trivy.yaml' <<< "$LAST_OUTPUT"; then
    pass 'a root trivy.yaml is found'
else
    fail 'a root trivy.yaml is found'
fi
rm -f "$TMP_ROOT/fake-root/trivy.yaml"
mkdir -p "$TMP_ROOT/fake-root"
printf 'CVE-2026-78661\n' > "$TMP_ROOT/fake-root/.trivyignore.yaml"
LAST_OUTPUT="$(root_ignore_files "$TMP_ROOT/fake-root")"
if [ "$LAST_OUTPUT" = '.trivyignore.yaml' ]; then
    pass 'a root .trivyignore.yaml is found'
else
    fail 'a root .trivyignore.yaml is found'
fi

echo
if [ "$failures" -ne 0 ]; then
    printf '%d of %d case(s) FAILED.\n' "$failures" "$cases"
    exit 1
fi
printf 'All %d list-third-party-images case(s) passed.\n' "$cases"
