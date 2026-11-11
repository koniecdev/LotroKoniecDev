# shellcheck shell=bash
# The settings of the repository this copy of the engine runs in: scripts/claude-loop.conf, next to
# this folder (CONTRACT.md, section 2). Every engine script that talks to GitHub sources this
# file and calls load_loop_config first. The settings file is read line by line and never run.

# The contract version this engine speaks (CONTRACT.md). A repository's settings must name it.
LOOP_CONTRACT=1

# Every key of scripts/claude-loop.conf, each one required.
LOOP_CONFIG_KEYS="contract repo gh_user claude_config_dir provenance_gate trusted_associations trusted_logins require_private red_main_workflow code_scanning e2e_image_label skip_labels skip_titles skip_issues"

# What load_loop_config sets; the scripts that source this file read them.
# shellcheck disable=SC2034
LOOP_REPO="" LOOP_CLAUDE_CONFIG_DIR="" LOOP_CFG_contract="" LOOP_CFG_repo="" LOOP_CFG_gh_user="" \
    LOOP_CFG_claude_config_dir="" LOOP_CFG_provenance_gate="" LOOP_CFG_trusted_associations="" \
    LOOP_CFG_trusted_logins="" LOOP_CFG_require_private="" LOOP_CFG_red_main_workflow="" \
    LOOP_CFG_code_scanning="" LOOP_CFG_e2e_image_label="" LOOP_CFG_skip_labels="" \
    LOOP_CFG_skip_titles="" LOOP_CFG_skip_issues=""

# loop_config_shape_ok <key> <value> — whether the value has the shape its key takes.
loop_config_shape_ok() {
    local value="$2"
    case "$1" in
        contract) [[ "$value" =~ ^[1-9][0-9]*$ ]] ;;
        repo) [[ "$value" =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$ ]] ;;
        gh_user) [[ "$value" =~ ^[A-Za-z0-9-]*$ ]] ;;
        claude_config_dir) [ -n "$value" ] ;;
        provenance_gate|require_private|code_scanning) [[ "$value" =~ ^(on|off)$ ]] ;;
        trusted_associations) [[ "$value" =~ ^([A-Z_]+(,[A-Z_]+)*)?$ ]] ;;
        trusted_logins) [[ "$value" =~ ^([A-Za-z0-9-]+(,[A-Za-z0-9-]+)*)?$ ]] ;;
        red_main_workflow) [[ "$value" =~ ^([A-Za-z0-9_.-]+\.ya?ml)?$ ]] ;;
        e2e_image_label) [[ "$value" =~ ^[A-Za-z0-9_.-]*$ ]] ;;
        skip_labels|skip_titles) return 0 ;;
        skip_issues) [[ "$value" =~ ^([0-9]+( [0-9]+)*)?$ ]] ;;
        *) return 1 ;;
    esac
}

# loop_origin_repo <checkout> — prints owner/name from the checkout's origin URL as its config
# holds it, or nothing when that is not a GitHub URL.
loop_origin_repo() {
    local url
    url="$(git -C "$1" config --get remote.origin.url 2>/dev/null || true)"
    case "$url" in
        https://github.com/*) url="${url#https://github.com/}" ;;
        git@github.com:*) url="${url#git@github.com:}" ;;
        ssh://git@github.com/*) url="${url#ssh://git@github.com/}" ;;
        *) return 0 ;;
    esac
    url="${url%/}"
    echo "${url%.git}"
}

# load_loop_config <checkout> [--no-origin] — reads <checkout>/scripts/claude-loop.conf into
# LOOP_CFG_<key>, and sets LOOP_REPO and LOOP_CLAUDE_CONFIG_DIR. Prints every problem it finds and
# fails when there is one. --no-origin skips the check that `repo` is the checkout's origin, for a
# check of the file alone: a fork's clone has another origin.
load_loop_config() {
    local checkout="$1" check_origin=1 file line key value seen=" " problems="" origin lower_origin lower_repo
    [ "${2:-}" != --no-origin ] || check_origin=0
    file="$checkout/scripts/claude-loop.conf"
    if [ ! -f "$file" ]; then
        echo "loop settings: $file is missing (scripts/claude/CONTRACT.md, section 2)" >&2
        return 1
    fi
    while IFS= read -r line || [ -n "$line" ]; do
        line="${line%$'\r'}"
        case "$line" in ''|'#'*) continue ;; esac
        case "$line" in
            *=*) ;;
            *) problems="$problems"$'\n'"  not a key=value line: $line"; continue ;;
        esac
        key="${line%%=*}"
        value="${line#*=}"
        case " $LOOP_CONFIG_KEYS " in
            *" $key "*) ;;
            *) problems="$problems"$'\n'"  unknown key: $key"; continue ;;
        esac
        case "$seen" in
            *" $key "*) problems="$problems"$'\n'"  key given twice: $key"; continue ;;
        esac
        seen="$seen$key "
        if ! loop_config_shape_ok "$key" "$value"; then
            problems="$problems"$'\n'"  $key has a value of the wrong shape: $value"
            continue
        fi
        printf -v "LOOP_CFG_$key" '%s' "$value"
    done < "$file"
    for key in $LOOP_CONFIG_KEYS; do
        case "$seen" in
            *" $key "*) ;;
            *) problems="$problems"$'\n'"  missing key: $key" ;;
        esac
    done
    if [ -z "$problems" ] && [ "$LOOP_CFG_contract" != "$LOOP_CONTRACT" ]; then
        problems="$problems"$'\n'"  contract=$LOOP_CFG_contract, but this engine speaks contract $LOOP_CONTRACT: update the prompts and the settings to it (scripts/claude/CONTRACT.md)"
    fi
    if [ -z "$problems" ] && [ "$LOOP_CFG_provenance_gate" = "on" ] \
        && [ -z "$LOOP_CFG_trusted_associations$LOOP_CFG_trusted_logins" ]; then
        problems="$problems"$'\n'"  provenance_gate=on trusts nobody: set trusted_associations or trusted_logins"
    fi
    if [ -z "$problems" ] && [ "$check_origin" -eq 1 ]; then
        origin="$(loop_origin_repo "$checkout")"
        lower_origin="$(printf '%s' "$origin" | tr '[:upper:]' '[:lower:]')"
        lower_repo="$(printf '%s' "$LOOP_CFG_repo" | tr '[:upper:]' '[:lower:]')"
        if [ "$lower_origin" != "$lower_repo" ]; then
            problems="$problems"$'\n'"  repo=$LOOP_CFG_repo, but the origin of $checkout is ${origin:-not a GitHub repository}"
        fi
    fi
    if [ -n "$problems" ]; then
        echo "loop settings: $file is not usable:$problems" >&2
        return 1
    fi
    LOOP_REPO="$LOOP_CFG_repo"
    # shellcheck disable=SC2034  # read by the worker
    case "$LOOP_CFG_claude_config_dir" in
        /*) LOOP_CLAUDE_CONFIG_DIR="$LOOP_CFG_claude_config_dir" ;;
        *) LOOP_CLAUDE_CONFIG_DIR="$HOME/$LOOP_CFG_claude_config_dir" ;;
    esac
}

# Every gh call of the engine goes to the configured repository, whatever folder it runs in. A
# function, not an exported GH_REPO, so the session's own gh calls stay as they are. `gh repo view`
# does not read GH_REPO, so the engine names the repository there, and in every `gh api` path.
gh() {
    GH_REPO="${LOOP_REPO:-}" command gh "$@"
}

# mint_gh_token <log function> — exports the token of the settings' gh_user (or LOOP_GH_USER) as
# GH_TOKEN, unless the caller set one. The active gh account may be one that cannot see the
# repository or write to it, such as a work account that GitHub bars from anything outside its
# enterprise, so every gh call of a run uses one known account. An empty gh_user keeps the active
# account.
mint_gh_token() {
    local user="${LOOP_GH_USER:-${LOOP_CFG_gh_user:-}}" token
    [ -z "${GH_TOKEN:-}" ] || return 0
    [ -n "$user" ] || return 0
    token="$(command gh auth token --user "$user" 2>/dev/null || true)"
    if [ -n "$token" ]; then
        export GH_TOKEN="$token"
    else
        "$1" "no gh token for '$user' — gh calls will use the active gh account"
    fi
}
