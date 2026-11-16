#!/usr/bin/env bash
#
# Resolves one image tag to the digests of the four deployable images and checks that each digest
# was built and attested by this repository's cd.yml, from the same git ref as the run that is
# deploying (#1336). On success it prints one line, `EXPECTED_DIGESTS=<repo@digest …>`, for
# deploy.sh to compare with what it pulls. On any failure it prints nothing to stdout and exits
# non-zero, so a caller that appends stdout to $GITHUB_ENV never gets a partial list.
#
# Why the signer and the ref are pinned, and not only the repository: any branch of this repository
# can run its own cd.yml, push an attested image to GHCR and move a `sha-<short>` tag. `--repo`
# alone accepts that image. CD runs deploy jobs only from `main` (the environments' branch rule),
# so `--source-ref "$GITHUB_REF"` means "built from main" for every deploy that starts.
#
# Callers: deploy.yml (the rollout and its automatic rollback) and the runbook's hand rollback.
#
# Usage:  resolve-verified-digests.sh <image-tag>
# Env:    REGISTRY, IMAGE_NAMESPACE, GITHUB_REPOSITORY, GITHUB_REF (all required); `gh` needs a token
#         (GH_TOKEN in CI) and `docker` a GHCR login when the packages are private.
# Exit:   0 = verified, 1 = an image did not resolve or did not pass the check, 2 = usage error.
#
# CI-only (Linux runners), so it has no .ps1 twin.
set -euo pipefail
export LC_ALL=C

IMAGES=(
    lotrokoniecdev-migrator
    lotrokoniecdev-auth-api
    lotrokoniecdev-tms-api
    lotrokoniecdev-frontend
)

tag="${1:-}"

for name in REGISTRY IMAGE_NAMESPACE GITHUB_REPOSITORY GITHUB_REF; do
    if [ -z "${!name:-}" ]; then
        echo "::error::resolve-verified-digests.sh: ${name} is not set. An empty value would weaken the check." >&2
        exit 2
    fi
done

if [[ ! "$tag" =~ ^[A-Za-z0-9._-]+$ ]]; then
    echo "::error::resolve-verified-digests.sh: refusing image tag '${tag}'. A GHCR tag is [A-Za-z0-9._-] only." >&2
    exit 2
fi

signer_workflow="${GITHUB_REPOSITORY}/.github/workflows/cd.yml"
digests=""

for image in "${IMAGES[@]}"; do
    repo="${REGISTRY}/${IMAGE_NAMESPACE}/${image}"
    ref="${repo}:${tag}"
    # `printf`, not a bare `{{.Manifest.Digest}}`: some buildx builds (Ubuntu's 0.30.1) treat a format
    # that starts with `{{.Manifest` as a request for the full text view.
    digest="$(docker buildx imagetools inspect "$ref" --format '{{printf "%s" .Manifest.Digest}}')" || {
        echo "::error::Could not resolve a digest for ${ref}." >&2
        exit 1
    }
    # The digest goes into an ssh command later, so only the exact sha256 shape may pass.
    if [[ ! "$digest" =~ ^sha256:[0-9a-f]{64}$ ]]; then
        echo "::error::Could not resolve a digest for ${ref} (got '${digest:-<empty>}')." >&2
        exit 1
    fi
    printf '%s -> %s\n' "$ref" "$digest" >&2

    echo "::group::Verify provenance: ${repo}@${digest}" >&2
    if ! gh attestation verify "oci://${repo}@${digest}" \
        --repo "$GITHUB_REPOSITORY" \
        --signer-workflow "$signer_workflow" \
        --source-ref "$GITHUB_REF" >&2; then
        echo "::endgroup::" >&2
        echo "::error::${ref} was not built and attested by ${signer_workflow} from ${GITHUB_REF}. Refusing it." >&2
        exit 1
    fi
    echo "::endgroup::" >&2

    digests="${digests}${digests:+ }${repo}@${digest}"
done

printf 'EXPECTED_DIGESTS=%s\n' "$digests"
