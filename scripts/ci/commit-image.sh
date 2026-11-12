#!/usr/bin/env bash
# The two registry decisions of cd.yml's build-and-push job, which builds each commit once (ADR-0070).
#
#   find  Is this commit's image already in GHCR? Writes `exists=true` (reuse it) or `exists=false`
#         (build it) to $GITHUB_OUTPUT. The job names an image only after Trivy, signing and the
#         attestation passed (`name`), so a tag CD wrote is a checked image. A tag whose provenance
#         does not verify stops the job: CD never builds over a tag.
#   name  Adds the tags to the checked digest, as the job's last step. It never moves an existing
#         tag to another digest, except `latest`, and reads every tag back afterwards.
#
# Input (env): IMAGE_REPO (ghcr.io/<owner>/<image>). find: IMAGE_TAG (sha-<short>),
# GITHUB_REPOSITORY, GITHUB_OUTPUT. name: DIGEST (sha256:…) and TAGS (full refs, one per line).
# `docker` and `gh` come from PATH, so the tests replace both. Exit 0 = decided, 1 = stop the job.
#
# CI-only (Linux runners), like resolve-prod-baseline.sh, so no .ps1 twin.
set -euo pipefail
export LC_ALL=C

log() {
    printf '%s\n' "$*" >&2
}

require() {
    local name
    for name in "$@"; do
        if [ -z "${!name:-}" ]; then
            log "::error::commit-image.sh: ${name} is not set."
            exit 1
        fi
    done
}

is_digest() {
    [[ "$1" =~ ^sha256:[0-9a-f]{64}$ ]]
}

# Prints the digest $1 names and returns 0. Returns 3 when the registry says the tag is missing, or
# will not tell this token: a token that cannot read a package cannot write it either, so a build
# that follows can move no tag it could not see. Returns 1 on any other answer.
digest_of() {
    local ref="$1" error_file printed
    error_file="$(mktemp)"
    if printed="$(docker buildx imagetools inspect "$ref" --format '{{.Manifest.Digest}}' 2>"$error_file")"; then
        rm -f "$error_file"
        if is_digest "$printed"; then
            printf '%s\n' "$printed"
            return 0
        fi
        log "::error::Could not read the digest of ${ref} (got '${printed:-<empty>}')."
        return 1
    fi
    if grep -qE ': not found$|denied|unauthorized|401 Unauthorized|403 Forbidden' "$error_file"; then
        rm -f "$error_file"
        return 3
    fi
    cat "$error_file" >&2
    rm -f "$error_file"
    log "::error::Could not tell whether ${ref} exists. CD never builds over an image it cannot see; re-run the job when the registry answers."
    return 1
}

find_image() {
    require IMAGE_REPO IMAGE_TAG GITHUB_REPOSITORY GITHUB_OUTPUT
    local ref="${IMAGE_REPO}:${IMAGE_TAG}" found status=0
    found="$(digest_of "$ref")" || status=$?
    case "$status" in
        0)
            if gh attestation verify "oci://${IMAGE_REPO}@${found}" --repo "$GITHUB_REPOSITORY" >&2; then
                printf 'exists=true\n' >> "$GITHUB_OUTPUT"
                log "::notice::${ref} was built before (${found}). Nothing is built or pushed; the deploy uses that image."
                return 0
            fi
            log "::error::${ref} exists, but its build provenance did not verify. If the check failed for a moment, re-run the job. CD never builds over an existing tag: see the runbook, 'An image that failed its checks'."
            return 1
            ;;
        3)
            printf 'exists=false\n' >> "$GITHUB_OUTPUT"
            return 0
            ;;
        *)
            return 1
            ;;
    esac
}

name_image() {
    require IMAGE_REPO DIGEST TAGS
    if ! is_digest "$DIGEST"; then
        log "::error::DIGEST '${DIGEST}' is not a sha256 digest."
        return 1
    fi
    local tag current status args=() names=()
    while IFS= read -r tag; do
        [ -n "$tag" ] || continue
        case "$tag" in
            "${IMAGE_REPO}:"*) ;;
            *)
                log "::error::Tag '${tag}' is not in ${IMAGE_REPO}."
                return 1
                ;;
        esac
        if [ "$tag" != "${IMAGE_REPO}:latest" ]; then
            status=0
            current="$(digest_of "$tag")" || status=$?
            case "$status" in
                0)
                    if [ "$current" != "$DIGEST" ]; then
                        log "::error::${tag} already names ${current}, not ${DIGEST}. CD never moves an existing tag (ADR-0070)."
                        return 1
                    fi
                    ;;
                3) ;;
                *) return 1 ;;
            esac
        fi
        args+=(--tag "$tag")
        names+=("$tag")
    done <<< "$TAGS"
    if [ "${#names[@]}" -eq 0 ]; then
        log "::error::TAGS names no tag."
        return 1
    fi

    # One source that is an image index is copied as it is, so the tags name the signed digest.
    docker buildx imagetools create "${args[@]}" "${IMAGE_REPO}@${DIGEST}" >&2

    for tag in "${names[@]}"; do
        status=0
        current="$(digest_of "$tag")" || status=$?
        if [ "$status" -ne 0 ] || [ "$current" != "$DIGEST" ]; then
            log "::error::After tagging, ${tag} does not name ${DIGEST} (got '${current:-<none>}')."
            return 1
        fi
        log "${tag} -> ${DIGEST}"
    done
}

case "${1-}" in
    find) find_image ;;
    name) name_image ;;
    *)
        log "usage: commit-image.sh find|name"
        exit 1
        ;;
esac
