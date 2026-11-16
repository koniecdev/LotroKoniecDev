#!/usr/bin/env bash
#
# deploy.yml's automatic rollback (#1336): put the box back on the release it ran before a failed
# rollout or smoke. The config this run copied is always restored from `.previous/` first, so a broken
# Caddyfile never stays on the box. The images are rolled back only when the previous tag passes the
# same provenance check as a rollout (resolve-verified-digests.sh), and its checked digests go to
# deploy.sh, which refuses any other pulled image. Any branch run of cd.yml can move a `sha-<short>`
# tag, and a writer can make a staging rollout fail on purpose, so an unchecked rollback would pull
# whatever the tag names now.
#
# Env:   PREVIOUS_IMAGE_TAG, ENVIRONMENT, STACK_DIR (required), plus what resolve-verified-digests.sh
#        reads. `ssh box` comes from deploy.yml's "Configure ssh to the box" step.
# Exit:  0 = rolled back, 2 = usage error, anything else = not rolled back: 1 when the tag was not
#        verified (the config is still restored if ssh allows it), or the rollback's own ssh exit code.
#        The run summary says which.
#
# CI-only (Linux runners), so it has no .ps1 twin.
set -euo pipefail

for name in PREVIOUS_IMAGE_TAG ENVIRONMENT STACK_DIR; do
    if [ -z "${!name:-}" ]; then
        echo "::error::rollback-to-previous-tag.sh: ${name} is not set." >&2
        exit 2
    fi
done

summary() {
    cat >> "${GITHUB_STEP_SUMMARY:-/dev/null}" || true
}

echo "::error::Rollout or smoke failed — rolling ${HETZNER_HOST:-the box} back to ${PREVIOUS_IMAGE_TAG}."

verified=""
if result="$("$(dirname "$0")/resolve-verified-digests.sh" "$PREVIOUS_IMAGE_TAG")"; then
    verified="${result#EXPECTED_DIGESTS=}"
fi

restore_config="cd ${STACK_DIR} && for f in compose.hetzner.yaml .docker/hetzner/Caddyfile deploy.sh; do [ -f \".previous/\$f\" ] && cp -p \".previous/\$f\" \"\$f\" || true; done"
rc=0

# SC2029 (both ssh lines): the RUNNER must expand the tag, the digests and the stack dir into the
# remote command. The verified branch runs only after the check accepted the tag ([A-Za-z0-9._-]) and
# printed sha256 digests it checked. Intentional.
if [ -n "$verified" ]; then
    # shellcheck disable=SC2029
    ssh box "set -e; ${restore_config}; IMAGE_TAG='${PREVIOUS_IMAGE_TAG}' EXPECTED_DIGESTS='${verified}' bash ${STACK_DIR}/deploy.sh" || rc=$?
    if [ "$rc" -eq 0 ]; then
        summary <<EOF
### ⏮️ Rolled back

\`${ENVIRONMENT}\` was rolled back to image tag \`${PREVIOUS_IMAGE_TAG}\` after a failed rollout/smoke.
EOF
    else
        summary <<EOF
### ⚠️ Rollback failed

The rollback of \`${ENVIRONMENT}\` to \`${PREVIOUS_IMAGE_TAG}\` exited ${rc}. Check the box: runbook → "Deploy or roll back by hand".
EOF
    fi
    exit "$rc"
fi

# shellcheck disable=SC2029
ssh box "set -e; ${restore_config}" || rc=$?
if [ "$rc" -eq 0 ]; then
    config_line="The config (compose file, Caddyfile, deploy.sh) was restored from \`.previous/\`."
else
    config_line="Restoring the config from \`.previous/\` also failed (exit ${rc})."
fi
summary <<EOF
### ⚠️ Not rolled back

The previous image tag \`${PREVIOUS_IMAGE_TAG}\` could not be verified, so it was not deployed: either its provenance check refused it or its digests could not be looked up (the step log says which). \`${ENVIRONMENT}\` may still run the release that just failed.
${config_line}
Check \`docker compose ps\` on the box, then roll back by hand with a verified tag: runbook → "Deploy or roll back by hand".
EOF
echo "::error::Not rolled back: ${PREVIOUS_IMAGE_TAG} could not be verified (see the provenance check above)."
exit 1
