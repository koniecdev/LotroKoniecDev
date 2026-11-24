#!/usr/bin/env bash
# The third-party images the servers run, and the exception files of their daily scan (ADR-0076).
#
#   --compose <file>…   Prints the image ref of every line whose first text is `image:`. Our own
#                       ghcr.io/${IMAGE_NAMESPACE…} images are skipped: cd.yml and the other half of
#                       image-scan.yml scan them. Exit 2 when a file is missing, has no image, has
#                       a ref this reader cannot trust (another `${`, a quote, a comment, no tag),
#                       or has an image key in another form (`- image:`, `{image: …}`, `"image":`,
#                       `image :`).
#   --backup <file>     Prints the default of POSTGRES_IMAGE in scripts/hetzner/backup.sh. Exit 2
#                       unless exactly one such line holds a plain ref.
#   --matrix staging=<sha> production=<sha>
#                       Run from the repo. Reads compose.hetzner.yaml and backup.sh at each deployed
#                       commit and compose.observability.yaml from the working tree, and prints the
#                       scan matrix as one JSON line: {"include":[{"leg","ref","ignore","error"}…]}.
#                       A commit that is not a full SHA, or a file that cannot be read, becomes an
#                       item with an `error`, so only that leg fails and every other image is still
#                       scanned. The output is never empty. Exit 2 only on wrong arguments.
#   --check-exceptions [--today YYYY-MM-DD] <file>…
#                       Checks the exception files under .github/trivy/ (one per image; ADR-0076).
#                       Exit 1 when an entry breaks a rule, 2 on wrong arguments or a missing file.
#
# The exception check has no "already expired" rule on purpose: it runs on every PR, and a date
# that passes must never turn an unrelated PR or main red. An expired entry makes Trivy report the
# finding again, so the daily scan goes red instead.
#
# CI-only (Linux runners), like commit-image.sh, so no .ps1 twin.
# shellcheck disable=SC2016 # `${` and jq's `$name` are literal text here, never shell expansions.
set -euo pipefail
export LC_ALL=C

readonly REF_PATTERN='^[a-z0-9][a-z0-9._/-]*:[A-Za-z0-9._-]+(@sha256:[0-9a-f]{64})?$'
readonly OTHER_IMAGE_FORM="(^|[^A-Za-z0-9_-])[\"']?image[\"']?[[:space:]]*:"
readonly OUR_IMAGES_PREFIX='ghcr.io/${IMAGE_NAMESPACE'
readonly TRIVY_DIR='.github/trivy'
readonly ENVIRONMENTS=(staging production)

log() {
    printf '%s\n' "$*" >&2
}

usage() {
    log "list-third-party-images.sh: $*"
    log 'Usage: --compose <file>… | --backup <file> | --matrix staging=<sha> production=<sha> | --check-exceptions [--today YYYY-MM-DD] <file>…'
    exit 2
}

check_ref() {
    local ref="$1" where="$2"
    if [[ "$ref" == *'${'* ]]; then
        log "${where}: '${ref}' takes its value from a variable. Only our own ${OUR_IMAGES_PREFIX}…} images may, because the scan cannot know the value."
        return 1
    fi
    if [[ ! "$ref" =~ $REF_PATTERN ]]; then
        log "${where}: '${ref}' is not a plain image ref (name:tag or name:tag@sha256:<digest>). A quote, a comment or a missing tag stops the list."
        return 1
    fi
}

read_compose() {
    local file="$1" name="$2" line ref number=0 count=0
    if [ ! -f "$file" ] || [ ! -r "$file" ]; then
        log "${name}: the file is missing or cannot be read."
        return 2
    fi
    while IFS= read -r line || [ -n "$line" ]; do
        number=$((number + 1))
        if [[ ! "$line" =~ ^[[:space:]]*image:(.*)$ ]]; then
            # `- image: x`, `{image: x}`, `"image": x` and `image : x` are images too. Reading only
            # the plain form would skip them without a word, so these forms stop the list.
            if [[ ! "$line" =~ ^[[:space:]]*# ]] && [[ "$line" =~ $OTHER_IMAGE_FORM ]]; then
                log "${name}:${number}: an image key in a form this reader does not follow (a list item, a flow mapping or after other text). Write it as its own \`image: <ref>\` line."
                return 2
            fi
            continue
        fi
        ref="${BASH_REMATCH[1]}"
        ref="${ref#"${ref%%[![:space:]]*}"}"
        ref="${ref%"${ref##*[![:space:]]}"}"
        [[ "$ref" == "$OUR_IMAGES_PREFIX"* ]] && continue
        check_ref "$ref" "${name}:${number}" || return 2
        printf '%s\n' "$ref"
        count=$((count + 1))
    done < "$file"
    if [ "$count" -eq 0 ]; then
        log "${name}: no third-party image found. A file the servers use with no image is not what this list expects."
        return 2
    fi
}

read_backup() {
    local file="$1" name="$2" matches ref
    if [ ! -f "$file" ] || [ ! -r "$file" ]; then
        log "${name}: the file is missing or cannot be read."
        return 2
    fi
    matches="$(grep -E '^POSTGRES_IMAGE=' "$file" || true)"
    if [ -z "$matches" ] || [ "$(wc -l <<< "$matches")" -ne 1 ]; then
        log "${name}: expected exactly one line that sets POSTGRES_IMAGE."
        return 2
    fi
    if [[ ! "$matches" =~ ^POSTGRES_IMAGE=\"\$\{POSTGRES_IMAGE:-([^}]*)\}\"$ ]]; then
        log "${name}: the POSTGRES_IMAGE line is not POSTGRES_IMAGE=\"\${POSTGRES_IMAGE:-<ref>}\"."
        return 2
    fi
    ref="${BASH_REMATCH[1]}"
    check_ref "$ref" "$name" || return 2
    printf '%s\n' "$ref"
}

# ── --matrix ───────────────────────────────────────────────────────────────────────────────────
declare -a ITEM_REFS=()
declare -A ITEM_LABELS=()
declare -a ERROR_LEGS=()
declare -a ERROR_MESSAGES=()

add_item() {
    local ref="$1" label="$2"
    if [ -z "${ITEM_LABELS[$ref]+set}" ]; then
        ITEM_REFS+=("$ref")
        ITEM_LABELS[$ref]="$label"
        return
    fi
    case ", ${ITEM_LABELS[$ref]}, " in
        *", ${label}, "*) ;;
        *) ITEM_LABELS[$ref]="${ITEM_LABELS[$ref]}, ${label}" ;;
    esac
}

add_error() {
    ERROR_LEGS+=("$1")
    ERROR_MESSAGES+=("$2")
}

# Reads one file through one reader and adds its refs, or one error item that says why not.
collect() {
    local label="$1" reader="$2" file="$3" name="$4" refs ref
    if ! refs="$("$reader" "$file" "$name" 2> "$WORK/reader.err")"; then
        add_error "${label}: cannot list ${name%% *}" "$(< "$WORK/reader.err")"
        return
    fi
    while IFS= read -r ref; do
        add_item "$ref" "$label"
    done <<< "$refs"
}

leg_name() {
    local ref="$1"
    if [[ "$ref" =~ ^(.*)@sha256:([0-9a-f]{12})[0-9a-f]{52}$ ]]; then
        ref="${BASH_REMATCH[1]}@${BASH_REMATCH[2]}"
    fi
    printf '%s' "$ref"
}

ignore_file() {
    local repository="${1%%@*}"
    repository="${repository%:*}"
    local file="${TRIVY_DIR}/${repository//\//-}.trivyignore.yaml"
    if [ -f "$file" ]; then
        printf '%s' "$file"
    fi
}

matrix() {
    local arg environment sha path reader root ref
    local -A commits=()
    [ "$#" -gt 0 ] || usage '--matrix needs staging=<sha> and production=<sha>.'
    for arg in "$@"; do
        [[ "$arg" == *=* ]] || usage "--matrix argument '${arg}' is not <environment>=<sha>."
        environment="${arg%%=*}"
        case "$environment" in
            staging | production) ;;
            *) usage "--matrix knows only staging and production, not '${environment}'." ;;
        esac
        [ -z "${commits[$environment]+set}" ] || usage "--matrix got ${environment} twice."
        commits[$environment]="${arg#*=}"
    done
    for environment in "${ENVIRONMENTS[@]}"; do
        [ -n "${commits[$environment]+set}" ] || usage "--matrix needs ${environment}=<sha>, even when it is empty."
    done

    root="$(git rev-parse --show-toplevel 2> /dev/null)" || usage '--matrix runs inside the repo.'
    cd -- "$root"
    WORK="$(mktemp -d)"
    trap 'rm -rf "$WORK"' EXIT

    for environment in "${ENVIRONMENTS[@]}"; do
        sha="${commits[$environment]}"
        # An empty commit would make `git show :<path>` read the index, which is today's code and not
        # what the box runs: a quiet green. So only a full SHA is read.
        if [[ ! "$sha" =~ ^[0-9a-f]{40}$ ]]; then
            add_error "${environment}: no deployed commit" "The ${environment} deployed commit '${sha}' is not a full commit SHA, so the images that ${environment} runs cannot be read. The resolve job says why."
            continue
        fi
        if ! git cat-file -e "${sha}^{commit}" 2> /dev/null; then
            add_error "${environment}: unknown commit" "The ${environment} deployed commit ${sha} is not in this checkout, so the images that ${environment} runs cannot be read."
            continue
        fi
        for path in compose.hetzner.yaml scripts/hetzner/backup.sh; do
            if ! git show "${sha}:${path}" > "$WORK/file" 2> "$WORK/show.err"; then
                add_error "${environment}: cannot list ${path}" "${path} cannot be read at the ${environment} deployed commit ${sha}: $(< "$WORK/show.err")"
                continue
            fi
            reader=read_compose
            [ "$path" = 'scripts/hetzner/backup.sh' ] && reader=read_backup
            collect "$environment" "$reader" "$WORK/file" "${path} at ${sha:0:12}"
        done
    done
    collect observability read_compose compose.observability.yaml 'compose.observability.yaml'

    local index message
    {
        for ref in "${ITEM_REFS[@]}"; do
            printf '%s\t%s\t%s\t\n' "$(leg_name "$ref") (${ITEM_LABELS[$ref]})" "$ref" "$(ignore_file "$ref")"
        done
        for index in "${!ERROR_LEGS[@]}"; do
            # One line per item, so a multi-line message keeps its words on one line.
            message="${ERROR_MESSAGES[$index]//$'\n'/ }"
            printf '%s\t\t\t%s\n' "${ERROR_LEGS[$index]}" "${message//$'\t'/ }"
        done
    } | jq -cRn '{include: ([inputs | split("\t") | {leg: .[0], ref: .[1], ignore: .[2], error: .[3]}] | sort_by(.leg))}'
}

# ── --check-exceptions ─────────────────────────────────────────────────────────────────────────
# Every entry: exactly the keys below (paths optional), one package purl with no qualifiers, an
# expiry at midnight UTC that is a real date, and a statement whose prefix sets the longest allowed
# expiry. Trivy drops a key it does not know without a word, so `expires_at` would never expire.
# A false positive is proven for one package version, so its purl names that version: a new pin
# with another version gets its finding back instead of keeping the old proof for 180 days.
readonly CHECK_PROGRAM='
def problem($n; $id; $text): "entry \($n) (\($id)): \($text)";
def real_midnight: type == "string"
    and test("^[0-9]{4}-[0-9]{2}-[0-9]{2}T00:00:00Z$")
    and ((try (fromdateiso8601 | todateiso8601) catch "") == .);
def limit_days: if type != "string" then null
    elif startswith("Waiting for a vendor fix: ") then (if ltrimstr("Waiting for a vendor fix: ") | test("[^ ]") then 30 else null end)
    elif startswith("False positive: ") then (if ltrimstr("False positive: ") | test("[^ ]") then 180 else null end)
    else null end;
def entry_problems($n; $e):
    (if ($e.id | type) == "string" then $e.id else "?" end) as $id
    | (["expired_at", "id", "purls", "statement"] - ($e | keys)) as $missing
    | (($e | keys) - ["expired_at", "id", "paths", "purls", "statement"]) as $unknown
    | ($e.statement | limit_days) as $limit
    | (if ($missing | length) > 0 then problem($n; $id; "misses \($missing | join(", "))") else empty end),
      (if ($unknown | length) > 0 then problem($n; $id; "has the unknown key(s) \($unknown | join(", ")), which Trivy drops without a word") else empty end),
      (if ($e.id | type) != "string" or ($e.id | test("^(CVE-[0-9]{4}-[0-9]{4,}|GHSA-[0-9a-z]{4}-[0-9a-z]{4}-[0-9a-z]{4})$") | not)
         then problem($n; $id; "the id is not a CVE or GHSA id") else empty end),
      (if ($e.purls | type) != "array" or ($e.purls | length) != 1 or ($e.purls[0] | type) != "string"
          or ($e.purls[0] | test("^pkg:[a-z][a-z0-9.+-]*/[^@?#[:space:]]+(@[^?#[:space:]]+)?$") | not)
         then problem($n; $id; "purls must hold exactly one package purl with no qualifiers (?…) and no subpath (#…)") else empty end),
      (if $e | has("paths") then
          (if ($e.paths | type) != "array" or ($e.paths | length) == 0
              or any($e.paths[]; type != "string" or . == "" or startswith("/") or test("[*?\\[{]"))
             then problem($n; $id; "paths must be a non-empty list of plain paths inside the image, with no leading / and no wildcard") else empty end)
       else empty end),
      (if ($e.expired_at | real_midnight | not)
         then problem($n; $id; "expired_at must be a real date written as YYYY-MM-DDT00:00:00Z") else empty end),
      (if $limit == null
         then problem($n; $id; "the statement must start with \"Waiting for a vendor fix: \" or \"False positive: \", followed by the reason") else empty end),
      (if $limit != null and ($e.expired_at | real_midnight) and ($e.expired_at | fromdateiso8601) > ($today + $limit * 86400)
         then problem($n; $id; "expires \($e.expired_at), more than \($limit) days after \($today_text)") else empty end),
      (if $limit == 180 and ($e.purls | type) == "array" and ($e.purls | length) == 1 and ($e.purls[0] | type) == "string"
          and ($e.purls[0] | test("@[^?#[:space:]]+$") | not)
         then problem($n; $id; "a false positive is proven for one version, so its purl must name it (pkg:…@<version>)") else empty end);
if type != "object" or (keys != ["vulnerabilities"]) then "the file must be a JSON object whose only key is \"vulnerabilities\""
elif (.vulnerabilities | type) != "array" or (.vulnerabilities | length) == 0 then "\"vulnerabilities\" must be a non-empty list"
else
    (.vulnerabilities | to_entries[]
        | (.key + 1) as $n
        | if (.value | type) != "object" then problem($n; "?"; "is not an object") else entry_problems($n; .value) end),
    ([.vulnerabilities[]
        | select(type == "object" and (.id | type) == "string" and (.purls | type) == "array" and (.purls | length) == 1)
        | [.id, .purls[0]]]
        | group_by(.) | map(select(length > 1) | .[0])[]
        | "\(.[0]) for \(.[1]) is listed twice")
end
'

check_exceptions() {
    local today='' today_epoch file problems line failed=0
    if [ "${1-}" = '--today' ]; then
        [ "$#" -ge 2 ] || usage '--today needs a date.'
        today="$2"
        shift 2
    else
        today="$(date -u +%F)"
    fi
    [[ "$today" =~ ^[0-9]{4}-[0-9]{2}-[0-9]{2}$ ]] || usage "--today '${today}' is not YYYY-MM-DD."
    today_epoch="$(jq -nr --arg d "${today}T00:00:00Z" 'try ($d | fromdateiso8601 | if todateiso8601 == $d then . else empty end) catch empty')"
    [ -n "$today_epoch" ] || usage "--today '${today}' is not a real date."
    [ "$#" -gt 0 ] || usage '--check-exceptions needs at least one file.'
    for file in "$@"; do
        if [ ! -f "$file" ] || [ ! -r "$file" ]; then
            log "${file}: the file is missing or cannot be read."
            exit 2
        fi
    done
    for file in "$@"; do
        if ! jq -e 'true' "$file" > /dev/null 2>&1; then
            log "${file}: not valid JSON. Write the exceptions as JSON, so this check can read them (ADR-0076)."
            failed=1
            continue
        fi
        if ! problems="$(jq -r --argjson today "$today_epoch" --arg today_text "$today" "$CHECK_PROGRAM" "$file" 2>&1)"; then
            log "${file}: the check could not read the file: ${problems}"
            failed=1
            continue
        fi
        if [ -n "$problems" ]; then
            while IFS= read -r line; do
                log "${file}: ${line}"
            done <<< "$problems"
            failed=1
        fi
    done
    return "$failed"
}

[ "$#" -gt 0 ] || usage 'no mode given.'
mode="$1"
shift
case "$mode" in
    --compose)
        [ "$#" -gt 0 ] || usage '--compose needs at least one file.'
        for file in "$@"; do
            read_compose "$file" "$file" || exit 2
        done | awk '!seen[$0]++'
        ;;
    --backup)
        [ "$#" -eq 1 ] || usage '--backup takes exactly one file.'
        read_backup "$1" "$1" || exit 2
        ;;
    --matrix)
        matrix "$@"
        ;;
    --check-exceptions)
        check_exceptions "$@"
        ;;
    *)
        usage "unknown mode '${mode}'."
        ;;
esac
