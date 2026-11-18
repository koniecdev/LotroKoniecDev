#!/usr/bin/env bash
#
# The checks a desktop release passes before anything is built (spec 0016). desktop-release.yml runs
# it as the first step after checkout of a "Run workflow". It refuses:
#   * a ref other than exactly refs/heads/main (a writer can push a TAG named main, so never the
#     short name);
#   * a version that is not three plain numbers (no leading zeros, no prefix, no suffix, at most
#     nine digits a part, the same grammar as DesktopVersion in the app);
#   * 0.0.0, the developer build number, which never checks for updates;
#   * a version whose tag desktop-vX.Y.Z exists;
#   * a version not above the highest existing desktop-v tag (version order, so 0.10.0 > 0.9.0).
#
# Env:     GITHUB_REF, VERSION, GITHUB_REPOSITORY (all required); `gh` needs a token that can read
#          the repository (GH_TOKEN in CI).
# Output:  the reason on stderr as a GitHub ::error:: line; "Releasing desktop-vX.Y.Z ..." on stdout.
# Exit:    0 = the release may go on, 1 = refused, 2 = usage error or the tag list could not be read.
#
# CI-only (Linux and the Windows runner's Git Bash), so it has no .ps1 twin.
set -euo pipefail
export LC_ALL=C

for name in GITHUB_REF GITHUB_REPOSITORY; do
    if [ -z "${!name:-}" ]; then
        echo "::error::desktop-release-check.sh: ${name} is not set." >&2
        exit 2
    fi
done
VERSION="${VERSION-}"

refuse() {
    echo "::error::$1" >&2
    exit 1
}

pattern='^(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})$'

# The higher of two versions that both match the pattern.
higher_of() {
    printf '%s\n%s\n' "$1" "$2" | sort -t . -k 1,1n -k 2,2n -k 3,3n | tail -n 1
}

[ "$GITHUB_REF" = "refs/heads/main" ] \
    || refuse "A desktop release runs only from main, not from ${GITHUB_REF}."
[[ "$VERSION" =~ $pattern ]] \
    || refuse "The version must be three plain numbers such as 0.1.0, without a prefix."
[ "$VERSION" != "0.0.0" ] \
    || refuse "0.0.0 is the developer build number and is never released."

if ! refs="$(gh api "repos/${GITHUB_REPOSITORY}/git/matching-refs/tags/desktop-v" --jq '.[].ref')"; then
    echo "::error::desktop-release-check.sh: the list of desktop-v tags could not be read." >&2
    exit 2
fi

highest=""
while IFS= read -r ref; do
    [ -n "$ref" ] || continue
    [ "$ref" != "refs/tags/desktop-v${VERSION}" ] \
        || refuse "The tag desktop-v${VERSION} exists already."
    existing="${ref#refs/tags/desktop-v}"
    [[ "$existing" =~ $pattern ]] || continue
    if [ -z "$highest" ]; then
        highest="$existing"
    else
        highest="$(higher_of "$highest" "$existing")"
    fi
done <<< "$refs"

if [ -n "$highest" ] && [ "$(higher_of "$highest" "$VERSION")" != "$VERSION" ]; then
    refuse "${VERSION} is not above the highest desktop release, ${highest}."
fi

echo "Releasing desktop-v${VERSION} (highest so far: ${highest:-none})."
