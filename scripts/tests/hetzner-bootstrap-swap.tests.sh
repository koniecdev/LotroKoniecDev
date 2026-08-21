#!/usr/bin/env bash
# Test suite for the swap leg of the Hetzner bootstrap (scripts/hetzner/bootstrap.sh — OBS-01 / #708).
#
# Swap was kept OUT of bootstrap.sh for a year on the grounds that "swapfile creation + /etc/fstab
# edits can't be faithfully proven" (the runbook, #504). They can — the same way scripts/hetzner/
# deploy.sh is proven: every real input the function touches (swapon, mkswap, fallocate, dd, sysctl)
# becomes a STUB first on PATH that records its arguments, and the paths it writes (the swapfile,
# fstab, the sysctl drop-in) are pointed at a throwaway root through the function's own variables.
#
# The property that matters is IDEMPOTENCE: bootstrap.sh promises a second pass on a live box changes
# nothing, and this leg mutates persistent state on the host. So the cases below assert the no-op
# shape from every direction a box can be in — swap already on, an fstab line already there, a
# swapfile enabled by hand, and swap coming from something that is not our file at all.
#
# CI runs this in the `guards` job, right next to the other bash gates.

set -euo pipefail

SCRIPTS_DIR="$(cd "$(dirname "$0")/.." && pwd)"
BOOTSTRAP_SH="$SCRIPTS_DIR/hetzner/bootstrap.sh"
TMP_ROOT="$(mktemp -d)"
trap 'rm -rf "$TMP_ROOT"' EXIT

cases=0
CASE=""
LAST_OUTPUT=""
LAST_STATUS=0

fail() {
    printf '✗ [%s] %s\n' "$CASE" "$1"
    if [ -n "${2:-}" ]; then
        printf '%s\n' "$2" | sed 's/^/    /'
    fi
    printf '  --- stub calls ---\n'
    sed 's/^/    /' "$BOX/calls.log" 2>/dev/null || true
    printf '  --- fstab ---\n'
    sed 's/^/    /' "$BOX/fstab" 2>/dev/null || true
    printf '  --- ensure_swap output ---\n%s\n' "$LAST_OUTPUT" | sed 's/^/    /'
    exit 1
}

pass() {
    cases=$((cases + 1))
    printf '✓ [%s] %s\n' "$CASE" "$1"
}

# One throwaway "box": a fake root plus stubs for every tool the leg shells out to. $1 is what
# `swapon --show` reports before the run — empty for a box with no swap at all, a NAME line for one
# that already has some.
new_box() {
    # Exported: the stubs are child processes and read $BOX to find their call log.
    export BOX="$TMP_ROOT/box-$((++box_seq))"
    mkdir -p "$BOX/bin"
    : > "$BOX/calls.log"
    printf '%s' "${1:-}" > "$BOX/active-swap"
    cat > "$BOX/fstab" <<'EOF'
/dev/disk/by-uuid/dce03124-bc1e-47f1-8767-b5bf64a7664a / ext4 defaults 0 1
EOF

    # swapon: --show reads the box's current swap state, everything else records and (unless the
    # case says otherwise) marks the given file active — which is what the second run must see.
    cat > "$BOX/bin/swapon" <<'EOF'
#!/usr/bin/env bash
echo "swapon $*" >> "$BOX/calls.log"
case "${1:-}" in
    --show*)
        cat "$BOX/active-swap"
        exit 0
        ;;
esac
# Something else won a race for this file between our --show probe and this call.
if [ "${SWAPON_RACE:-0}" = 1 ]; then
    printf '%s\n' "$1" > "$BOX/active-swap"
    echo "swapon: $1: Device or resource busy" >&2
    exit 255
fi
# The kernel refuses an area it is already using (EBUSY) — modelling that as success would hide the
# one ordering that corrupts a live box.
if grep -qxF "$1" "$BOX/active-swap" 2> /dev/null; then
    echo "swapon: $1: Device or resource busy" >&2
    exit 255
fi
if [ "${SWAPON_NEEDS_MKSWAP:-0}" = 1 ] && [ ! -f "$BOX/mkswap-ran" ]; then
    echo "swapon: $1: read swap header failed" >&2
    exit 255
fi
printf '%s\n' "$1" > "$BOX/active-swap"
EOF

    cat > "$BOX/bin/mkswap" <<'EOF'
#!/usr/bin/env bash
echo "mkswap $*" >> "$BOX/calls.log"
: > "$BOX/mkswap-ran"
EOF

    # fallocate/dd both just materialize the file — the point is WHICH one ran, and that the
    # fallback leaves a file of the requested size behind.
    cat > "$BOX/bin/fallocate" <<'EOF'
#!/usr/bin/env bash
echo "fallocate $*" >> "$BOX/calls.log"
[ "${FALLOCATE_FAILS:-0}" = 1 ] && exit 1
# Real size, because ensure_swap re-creates a file that is not the size it asked for. head, not dd:
# dd is stubbed too, and a stub calling a stub both recurses and pollutes the call log.
head -c $(( ${2%M} * 1024 * 1024 )) /dev/zero > "$3"
EOF

    cat > "$BOX/bin/dd" <<'EOF'
#!/usr/bin/env bash
echo "dd $*" >> "$BOX/calls.log"
out=""; count=0
for arg in "$@"; do
    case "$arg" in
        of=*) out="${arg#of=}" ;;
        count=*) count="${arg#count=}" ;;
    esac
done
[ "${DD_FAILS:-0}" = 1 ] && { : > "$out"; exit 1; }   # a full disk leaves a partial file behind
head -c $(( count * 1024 * 1024 )) /dev/zero > "$out"
EOF

    cat > "$BOX/bin/sysctl" <<'EOF'
#!/usr/bin/env bash
echo "sysctl $*" >> "$BOX/calls.log"
EOF

cat > "$BOX/bin/findmnt" <<'EOF'
#!/usr/bin/env bash
echo "findmnt $*" >> "$BOX/calls.log"
EOF

    chmod +x "$BOX"/bin/*
}

# Drives the real ensure_swap out of the real bootstrap.sh, through its documented source seam.
run_swap() {
    LAST_STATUS=0
    LAST_OUTPUT="$(
        PATH="$BOX/bin:$PATH" \
        BOOTSTRAP_SOURCE_ONLY=1 \
        SWAPFILE="$BOX/swapfile" \
        SWAP_SIZE_MB="${SWAP_SIZE_MB:-2}" \
        FSTAB="$BOX/fstab" \
        SWAPPINESS_CONF="$BOX/swappiness.conf" \
        FALLOCATE_FAILS="${FALLOCATE_FAILS:-0}" \
        DD_FAILS="${DD_FAILS:-0}" \
        SWAPON_NEEDS_MKSWAP="${SWAPON_NEEDS_MKSWAP:-0}" \
        SWAPON_RACE="${SWAPON_RACE:-0}" \
        bash -c '. "$0" && ensure_swap' "$BOOTSTRAP_SH" 2>&1
    )" || LAST_STATUS=$?
    if [ "${EXPECT_FAILURE:-0}" = 1 ]; then
        [ "$LAST_STATUS" -ne 0 ] || fail 'ensure_swap reported success where it had to abort' "$LAST_OUTPUT"
    else
        [ "$LAST_STATUS" -eq 0 ] || fail "ensure_swap exited $LAST_STATUS" "$LAST_OUTPUT"
    fi
}

calls_matching() { grep -cE "$1" "$BOX/calls.log" || true; }
# GNU stat FIRST: `stat -f` means "filesystem status" on coreutils, so the BSD form must never be the
# one that runs on the CI runner — it prints a block of fs facts and the compare can never hold.
file_mode() { stat -c '%a' "$1" 2> /dev/null || stat -f '%Lp' "$1"; }
# A leftover of the RIGHT size — the only kind ensure_swap reuses instead of replacing.
full_size_swapfile() { head -c $((2 * 1024 * 1024)) /dev/zero > "$BOX/swapfile"; }
fstab_swap_lines() { grep -cE '[[:space:]]swap[[:space:]]' "$BOX/fstab" || true; }

box_seq=0

# ---------------------------------------------------------------------------------------------
CASE='fresh box'
new_box ''
run_swap

[ "$(calls_matching '^fallocate -l 2M ')" -eq 1 ] || fail 'the swapfile is not fallocated at the requested size'
[ -f "$BOX/swapfile" ] || fail 'no swapfile was created'
[ "$(file_mode "$BOX/swapfile")" = '600' ] \
    || fail 'the swapfile is not chmod 600 — mkswap warns and any user could read swapped-out memory'
[ "$(calls_matching '^mkswap ')" -eq 1 ] || fail 'mkswap did not run on the new file'
[ "$(calls_matching "^swapon $BOX/swapfile$")" -eq 1 ] || fail 'the swapfile was never activated'
pass 'creates, formats and activates the swapfile at the requested size'

[ "$(fstab_swap_lines)" -eq 1 ] || fail 'the swapfile was not persisted in fstab'
grep -qE "^$BOX/swapfile none swap sw 0 0$" "$BOX/fstab" || fail 'the fstab entry has the wrong shape'
pass 'persists the swapfile in fstab so it survives a reboot'

grep -qE '^vm\.swappiness = 10$' "$BOX/swappiness.conf" || fail 'vm.swappiness=10 was not written'
[ "$(calls_matching '^sysctl -q -p ')" -eq 1 ] || fail 'the sysctl drop-in was written but never applied'
pass 'sets vm.swappiness=10 and applies it without a reboot'

# ---------------------------------------------------------------------------------------------
# The promise bootstrap.sh makes in its header, on the one leg that mutates persistent host state.
CASE='second pass on the same box'
: > "$BOX/calls.log"
run_swap

[ "$(calls_matching '^(fallocate|dd|mkswap) ')" -eq 0 ] || fail 'a re-run re-created or re-formatted the swapfile'
[ "$(calls_matching "^swapon $BOX/swapfile$")" -eq 0 ] || fail 'a re-run re-activated an active swapfile'
[ "$(fstab_swap_lines)" -eq 1 ] || fail 'a re-run duplicated the fstab entry'
[ "$(calls_matching '^sysctl ')" -eq 0 ] || fail 'a re-run re-applied an unchanged sysctl drop-in'
pass 'changes nothing at all'

# ---------------------------------------------------------------------------------------------
# A box whose swap is a partition already has its buffer. Creating a second one is waste,
# and writing an fstab line for a file that does not exist would fail every future `swapon -a`.
CASE='swap already active, from something that is not our file'
new_box '/dev/sda4'
run_swap

[ "$(calls_matching '^(fallocate|dd|mkswap) ')" -eq 0 ] || fail 'a second swap area was created next to the existing one'
[ -e "$BOX/swapfile" ] && fail 'a swapfile was created on a box that already had swap'
[ "$(fstab_swap_lines)" -eq 0 ] || fail 'fstab now points at a swapfile that does not exist'
pass 'leaves the existing swap alone and writes no fstab line'

grep -qE '^vm\.swappiness = 10$' "$BOX/swappiness.conf" || fail 'swappiness was skipped along with the swapfile'
pass 'still converges vm.swappiness'

# ---------------------------------------------------------------------------------------------
# Exactly the state the live pair was in before #708: swap enabled by hand, nothing in fstab, so
# the next reboot silently loses it.
CASE='swapfile enabled by hand, absent from fstab'
new_box ''
printf 'placeholder' > "$BOX/swapfile"
chmod 644 "$BOX/swapfile"
printf '%s' "$BOX/swapfile" > "$BOX/active-swap"
run_swap

[ "$(calls_matching '^(fallocate|dd|mkswap) ')" -eq 0 ] || fail 'the live swapfile was re-formatted under the running kernel'
[ "$(fstab_swap_lines)" -eq 1 ] || fail 'a hand-enabled swapfile was never persisted'
pass 'persists it without touching the live swap area'

[ "$(file_mode "$BOX/swapfile")" = '600' ] \
    || fail 'a hand-enabled swapfile was left world-readable — that is other processes memory on disk'
pass 'converges its mode to 600'

# ---------------------------------------------------------------------------------------------
CASE='filesystem cannot fallocate'
new_box ''
FALLOCATE_FAILS=1 run_swap
FALLOCATE_FAILS=0

[ "$(calls_matching '^dd if=/dev/zero .* count=2 ')" -eq 1 ] || fail 'no dd fallback after fallocate failed'
[ -f "$BOX/swapfile" ] || fail 'the dd fallback left no swapfile'
[ "$(calls_matching "^swapon $BOX/swapfile$")" -eq 1 ] || fail 'the dd-written swapfile was never activated'
pass 'falls back to dd and still ends with active swap'

# ---------------------------------------------------------------------------------------------
# An interrupted earlier run can leave a full-size file with no swap signature. swapon fails on it;
# mkswap must run THEN — never before, since mkswap over an active swap area corrupts live pages.
CASE='leftover swapfile with no signature'
new_box ''
full_size_swapfile
SWAPON_NEEDS_MKSWAP=1 run_swap
SWAPON_NEEDS_MKSWAP=0

[ "$(calls_matching '^(fallocate|dd) ')" -eq 0 ] || fail 'the existing file was overwritten instead of reused'
[ "$(calls_matching '^mkswap ')" -eq 1 ] || fail 'the unsigned leftover was never formatted'
ordered="$(grep -E "^(mkswap|swapon) $BOX/swapfile$" "$BOX/calls.log" | awk '{ print $1 }' | tr '\n' ',')"
[ "$ordered" = 'swapon,mkswap,swapon,' ] \
    || fail "expected swapon,mkswap,swapon on the existing file — got ${ordered:-<nothing>}"
pass 'recovers by formatting only after swapon refused the file'

# ---------------------------------------------------------------------------------------------
# The one edit in this leg that can stop a box from booting: `>>` onto a file whose last line has no
# newline glues the entry onto the root entry, and libmount cannot parse a 10-field line.
CASE='fstab without a trailing newline'
new_box ''
printf '%s' 'UUID=dce03124 / ext4 defaults 0 1' > "$BOX/fstab"
run_swap

[ "$(head -1 "$BOX/fstab")" = 'UUID=dce03124 / ext4 defaults 0 1' ] \
    || fail 'the swap entry was glued onto the root entry — that box does not boot'
[ "$(fstab_swap_lines)" -eq 1 ] || fail 'the swap entry did not land on its own line'
[ "$(calls_matching '^findmnt --verify --fstab$')" -eq 1 ] || fail 'the edited fstab was never verified'
pass 'keeps the entry on its own line and verifies the result'

# ---------------------------------------------------------------------------------------------
# A box swapping to a partition can still carry a stale /swapfile. An fstab line for a file that is
# not a swap area fails every `swapon -a` from the next boot on.
CASE='swap on a partition, stale swapfile still on disk'
new_box '/dev/sda4'
printf 'stale' > "$BOX/swapfile"
run_swap

[ "$(fstab_swap_lines)" -eq 0 ] || fail 'fstab now points at a file that is not a swap area'
[ "$(calls_matching '^mkswap ')" -eq 0 ] || fail 'the stale file was formatted on a box that already swaps'
pass 'persists nothing for a file that is not the active swap'

# ---------------------------------------------------------------------------------------------
# A run that died mid-write leaves a short file. Adopting it hands the box a fraction of the buffer
# it asked for and still reports success.
CASE='truncated leftover swapfile'
new_box ''
printf 'short' > "$BOX/swapfile"
run_swap

[ "$(wc -c < "$BOX/swapfile")" -eq $((2 * 1024 * 1024)) ] \
    || fail 'the truncated leftover was adopted instead of replaced'
[ "$(calls_matching '^(fallocate|dd) ')" -eq 1 ] || fail 'the leftover was not re-created'
[ "$(calls_matching '^mkswap ')" -eq 1 ] || fail 'the replacement was never formatted'
pass 'replaces it with a full-size swapfile'

# ---------------------------------------------------------------------------------------------
CASE='disk fills up while writing the swapfile'
new_box ''
FALLOCATE_FAILS=1 DD_FAILS=1 EXPECT_FAILURE=1 run_swap
FALLOCATE_FAILS=0; DD_FAILS=0; EXPECT_FAILURE=0

[ -e "$BOX/swapfile" ] && fail 'a partial swapfile was left behind for the next run to adopt'
[ "$(fstab_swap_lines)" -eq 0 ] || fail 'fstab was edited even though no swap was ever created'
pass 'cleans up the partial file and edits nothing else'

# ---------------------------------------------------------------------------------------------
# Defense in depth: mkswap over an area the kernel is using corrupts live pages. The outer guard
# normally rules this out, so the case that reaches it is a race — something else enabled the file
# between our --show probe and our swapon.
CASE='another process enables the swapfile mid-run'
new_box ''
full_size_swapfile
SWAPON_RACE=1 EXPECT_FAILURE=1 run_swap
SWAPON_RACE=0; EXPECT_FAILURE=0

[ "$(calls_matching '^mkswap ')" -eq 0 ] || fail 'mkswap ran on an active swap area — that corrupts live pages'
printf '%s' "$LAST_OUTPUT" | grep -q 'refusing to mkswap' || fail 'the refusal was silent' "$LAST_OUTPUT"
pass 'refuses to format it and stops'

# ---------------------------------------------------------------------------------------------
# AC2 is the call site, not the function: the whole leg is dead code if bootstrap.sh stops calling it.
CASE='bootstrap wiring'
# `|| true`: a non-matching grep would trip pipefail and kill the suite before fail() could say why.
call_line="$(grep -n '^ensure_swap$' "$BOOTSTRAP_SH" | cut -d: -f1 || true)"
[ -n "$call_line" ] || fail 'bootstrap.sh never calls ensure_swap — a fresh box would get no swap'
apt_line="$(grep -n '^log "apt update' "$BOOTSTRAP_SH" | cut -d: -f1 || true)"
[ -n "$apt_line" ] && [ "$call_line" -lt "$apt_line" ] \
    || fail 'the swap leg no longer runs first — apt is a memory spike on a box with no buffer yet'
pass 'bootstrap.sh calls ensure_swap, before the first leg that needs the buffer'

# ---------------------------------------------------------------------------------------------
CASE='source seam'
new_box ''
LAST_STATUS=0
out="$(BOOTSTRAP_SOURCE_ONLY=1 bash -c '. "$0" && declare -F ensure_swap > /dev/null && echo defined' "$BOOTSTRAP_SH" 2>&1)" \
    || LAST_STATUS=$?
[ "$LAST_STATUS" -eq 0 ] && [ "$out" = 'defined' ] \
    || fail 'BOOTSTRAP_SOURCE_ONLY=1 did not stop before the root check' "$out"
pass 'defines the helpers and stops before touching the box'

printf '\n%d assertions passed.\n' "$cases"
