#!/usr/bin/env bash
#
# The clean-up after a desktop release job that failed or was cancelled after it made its tag (spec
# 0016). desktop-release.yml runs it in the release job when TAG_CREATED is true, so a stopped run
# never leaves a tag with no release. It deletes a draft release under the tag, then the tag.
#   * A release that went public before the stop stays, with its tag: apps may already have taken
#     its file, so the fix is the next version.
#   * Only gh's "release not found" means there is no release. Any other failure to ask keeps the
#     tag, because a public release may stand behind it.
#   * An answer that is neither true nor false keeps the tag too.
#
# Env:     VERSION, GH_REPO (both required); `gh` needs a token that can write the repository
#          (GH_TOKEN in CI).
# Output:  a GitHub ::warning:: line on stdout for every case that keeps the tag.
# Exit:    0 = the tag is gone, or kept on purpose; not 0 = a delete failed (the tag may stay);
#          2 = usage error.
#
# CI-only (Linux), so it has no .ps1 twin.
set -euo pipefail
export LC_ALL=C

for name in VERSION GH_REPO; do
    if [ -z "${!name:-}" ]; then
        echo "::error::desktop-release-cleanup.sh: ${name} is not set." >&2
        exit 2
    fi
done

tag="desktop-v$VERSION"
err_file="$(mktemp)"
trap 'rm -f "$err_file"' EXIT

if ! is_draft=$(gh release view "$tag" --json isDraft --jq '.isDraft' 2> "$err_file"); then
    if grep -qF 'release not found' "$err_file"; then
        is_draft=missing
    else
        echo "::warning::Could not check whether $tag has a release, so its tag stays. Check it by hand (runbook, 'Cutting a desktop release')."
        cat "$err_file"
        exit 0
    fi
fi

case "$is_draft" in
    false)
        echo "::warning::$tag was published before the job stopped; the release and its tag stay."
        exit 0
        ;;
    true)
        gh release delete "$tag" --yes
        ;;
    missing)
        ;;
    *)
        echo "::warning::Unexpected answer '$is_draft' about $tag, so its tag stays. Check it by hand."
        exit 0
        ;;
esac

gh api -X DELETE "repos/$GH_REPO/git/refs/tags/$tag"
echo "Removed the tag $tag."
