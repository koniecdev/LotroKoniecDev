#!/usr/bin/env bash
# Test suite for the night-reboot leg of the Hetzner bootstrap (scripts/hetzner/bootstrap.sh, #1327).
#
# The leg writes one apt config file, so the suite points it at a throwaway path and checks the
# file. What matters: a box without a time is refused (there is no default, because a default would
# be one box's slot), a box keeps its time across re-runs, a bad time never reaches the file, and apt
# reads the file the way we mean it.
#
# pr-verify and CI run this in their `build` job (the classifier's `guards` verdict), next to the
# other bootstrap suites.

set -euo pipefail

# With CDPATH set, `cd` to a relative path may print the folder, and that text would end up here (#1048).
SCRIPTS_DIR="$(CDPATH='' cd -- "$(dirname "$0")/.." && pwd)"
BOOTSTRAP_SH="$SCRIPTS_DIR/hetzner/bootstrap.sh"
TMP_ROOT="$(mktemp -d)"
trap 'rm -rf "$TMP_ROOT"' EXIT

cases=0
box_seq=0
CASE=""
CONF=""
LAST_OUTPUT=""
LAST_STATUS=0

fail() {
    printf '✗ [%s] %s\n' "$CASE" "$1"
    if [ -n "${2:-}" ]; then
        printf '%s\n' "$2" | sed 's/^/    /'
    fi
    printf '  --- config file ---\n'
    sed 's/^/    /' "$CONF" 2> /dev/null || printf '    (none)\n'
    printf '  --- ensure_unattended_reboot output ---\n%s\n' "$LAST_OUTPUT" | sed 's/^/    /'
    exit 1
}

pass() {
    cases=$((cases + 1))
    printf '✓ [%s] %s\n' "$CASE" "$1"
}

new_box() {
    box_seq=$((box_seq + 1))
    mkdir -p "$TMP_ROOT/box-$box_seq"
    CONF="$TMP_ROOT/box-$box_seq/52unattended-reboot"
}

# $1 = REBOOT_TIME for this run ('' = not set). Runs the real function from the source seam.
run_leg() {
    LAST_STATUS=0
    LAST_OUTPUT="$(REBOOT_TIME="$1" UNATTENDED_REBOOT_CONF="$CONF" \
        bash -c '. "$0" && ensure_unattended_reboot' "$BOOTSTRAP_SH" 2>&1)" || LAST_STATUS=$?
}

# Prints the time the file asks for, or nothing.
conf_time() {
    sed -n 's/^Unattended-Upgrade::Automatic-Reboot-Time "\(.*\)";$/\1/p' "$CONF" 2> /dev/null || true
}

# GNU stat FIRST: `stat -f` means "filesystem status" on coreutils (same note as the swap suite).
file_mode() { stat -c '%a' "$1" 2> /dev/null || stat -f '%Lp' "$1"; }

export BOOTSTRAP_SOURCE_ONLY=1

# ---------------------------------------------------------------------------------------------
CASE='fresh box, no REBOOT_TIME'
new_box
run_leg ''
[ "$LAST_STATUS" -ne 0 ] || fail "a box with no time got a default one, which is one of the two boxes' slots"
printf '%s' "$LAST_OUTPUT" | grep -q 'FATAL: this box has no reboot time yet' || fail 'the refusal does not say what to set'
[ ! -e "$CONF" ] || fail 'the refused run wrote the file'
pass 'refuses it and writes nothing'

# ---------------------------------------------------------------------------------------------
CASE='fresh box, REBOOT_TIME=02:30 (prod)'
new_box
run_leg '02:30'
[ "$LAST_STATUS" -eq 0 ] || fail "the leg failed (exit $LAST_STATUS)"
[ "$(conf_time)" = '02:30' ] || fail "got '$(conf_time)'"
grep -qx 'Unattended-Upgrade::Automatic-Reboot "true";' "$CONF" || fail 'the reboot is not switched on'
[ "$(file_mode "$CONF")" = '644' ] || fail "mode is $(file_mode "$CONF"), not 644"
pass 'switches the reboot on at 02:30 UTC, mode 644'

# ---------------------------------------------------------------------------------------------
CASE='fresh box, REBOOT_TIME=01:30 (staging)'
new_box
run_leg '01:30'
[ "$LAST_STATUS" -eq 0 ] || fail "the leg failed (exit $LAST_STATUS)"
[ "$(conf_time)" = '01:30' ] || fail "got '$(conf_time)'"
pass 'uses the given time'

# ---------------------------------------------------------------------------------------------
CASE='re-run without REBOOT_TIME on staging'
before="$(cat "$CONF")"
run_leg ''
[ "$LAST_STATUS" -eq 0 ] || fail "the re-run failed (exit $LAST_STATUS)"
[ "$(conf_time)" = '01:30' ] || fail "the re-run moved staging to '$(conf_time)'"
[ "$(cat "$CONF")" = "$before" ] || fail 'the re-run changed the file'
pass "keeps the box's own time and changes nothing"

# ---------------------------------------------------------------------------------------------
CASE='REBOOT_TIME on a box that already has a time'
run_leg '03:00'
[ "$LAST_STATUS" -eq 0 ] || fail "the leg failed (exit $LAST_STATUS)"
[ "$(conf_time)" = '03:00' ] || fail "got '$(conf_time)'"
pass 'moves the box to the new time'

# ---------------------------------------------------------------------------------------------
CASE='the first and the last minute of the day'
for edge in '00:00' '23:59'; do
    new_box
    run_leg "$edge"
    [ "$LAST_STATUS" -eq 0 ] || fail "'$edge' was refused"
    [ "$(conf_time)" = "$edge" ] || fail "'$edge' was written as '$(conf_time)'"
done
pass 'accepts 00:00 and 23:59'

# ---------------------------------------------------------------------------------------------
CASE='a time that is not HH:MM in UTC'
for bad in '24:00' '1:30' '01:60' '0130' 'now' '01:30 ' ' 01:30' 'x01:30' \
    '01:30"; Unattended-Upgrade::Automatic-Reboot "false' \
    'x"; Unattended-Upgrade::Automatic-Reboot "false"; //01:30'; do
    new_box
    run_leg "$bad"
    [ "$LAST_STATUS" -ne 0 ] || fail "'$bad' was accepted"
    printf '%s' "$LAST_OUTPUT" | grep -q 'FATAL: the reboot time must be HH:MM' || fail "the refusal of '$bad' does not say why"
    [ ! -e "$CONF" ] || fail "'$bad' was written to the file"
done
pass 'refuses each one and writes nothing'

# ---------------------------------------------------------------------------------------------
# Under a UTF-8 locale bash's [0-9] also matches digits such as ². systemd refuses such a time, so the
# box would quietly never reboot. Only a UTF-8 locale proves the case.
CASE='digits that are not ASCII, under a UTF-8 locale'
utf8_locale="$(locale -a 2> /dev/null | grep -iE '^(en_US|pl_PL|C)\.utf-?8$' | grep -viE '^C\.' | head -1 || true)"
if [ -n "$utf8_locale" ]; then
    for bad in '0²:30' '0١:30' '01:3½'; do
        new_box
        LC_ALL="$utf8_locale" run_leg "$bad"
        [ "$LAST_STATUS" -ne 0 ] || fail "'$bad' was accepted under $utf8_locale"
        [ ! -e "$CONF" ] || fail "'$bad' was written to the file"
    done
    pass "refuses each one under $utf8_locale"
else
    printf -- '- [%s] not run: no en_US or pl_PL UTF-8 locale on this machine\n' "$CASE"
fi

# ---------------------------------------------------------------------------------------------
CASE='a bad time on a box that already has one'
new_box
run_leg '01:30'
before="$(cat "$CONF")"
run_leg '25:00'
[ "$LAST_STATUS" -ne 0 ] || fail "'25:00' was accepted"
[ "$(cat "$CONF")" = "$before" ] || fail 'the refused run changed the file'
pass 'keeps the file as it was'

# ---------------------------------------------------------------------------------------------
# A kept time goes through the same check as a given one: a file edited by hand to "now" would make
# the box reboot at once, in the daytime, during the ~06:00 UTC upgrade run.
CASE='a kept time that is not HH:MM'
for kept in 'now' '25:00'; do
    new_box
    printf 'Unattended-Upgrade::Automatic-Reboot "true";\nUnattended-Upgrade::Automatic-Reboot-Time "%s";\n' "$kept" > "$CONF"
    before="$(cat "$CONF")"
    run_leg ''
    [ "$LAST_STATUS" -ne 0 ] || fail "the kept time '$kept' passed"
    [ "$(cat "$CONF")" = "$before" ] || fail "the refused run changed the file holding '$kept'"
done
pass 'refuses it and leaves the file as it was'

# ---------------------------------------------------------------------------------------------
CASE='hand-edited file with no readable time'
new_box
printf 'Unattended-Upgrade::Automatic-Reboot "true";\n' > "$CONF"
before="$(cat "$CONF")"
run_leg ''
[ "$LAST_STATUS" -ne 0 ] || fail 'the leg made up a time'
printf '%s' "$LAST_OUTPUT" | grep -q 'FATAL: this box has no reboot time yet' || fail 'the refusal does not say why'
[ "$(cat "$CONF")" = "$before" ] || fail 'the refused run changed the file'
pass 'refuses to guess'

# ---------------------------------------------------------------------------------------------
# The file is only worth something if apt reads it as meant. apt-config exists on Ubuntu (the CI
# runner, the boxes); elsewhere the case says it did not run.
CASE='apt reads the file'
if command -v apt-config > /dev/null; then
    new_box
    run_leg '01:30'
    dump="$(apt-config -c "$CONF" dump 2>&1)" || fail 'apt-config could not parse the file' "$dump"
    printf '%s\n' "$dump" | grep -qx 'Unattended-Upgrade::Automatic-Reboot "true";' \
        || fail 'apt does not see the reboot switched on' "$dump"
    printf '%s\n' "$dump" | grep -qx 'Unattended-Upgrade::Automatic-Reboot-Time "01:30";' \
        || fail 'apt does not see the reboot time' "$dump"
    pass 'apt-config sees the reboot on, at 01:30'
else
    printf -- '- [%s] not run: no apt-config on this machine (it runs on the Ubuntu CI runner)\n' "$CASE"
fi

# ---------------------------------------------------------------------------------------------
CASE='bootstrap wiring'
# `|| true`: a non-matching grep would trip pipefail and kill the suite before fail() could say why.
call_line="$(grep -n '^ensure_unattended_reboot$' "$BOOTSTRAP_SH" | cut -d: -f1 || true)"
[ -n "$call_line" ] || fail 'bootstrap.sh never calls ensure_unattended_reboot, so a fresh box never reboots'
seam_line="$(grep -n 'BOOTSTRAP_SOURCE_ONLY:-0' "$BOOTSTRAP_SH" | cut -d: -f1 || true)"
[ -n "$seam_line" ] && [ "$call_line" -gt "$seam_line" ] \
    || fail 'the call sits above the source seam, so sourcing the script would write to the box'
check_line="$(grep -n '^unattended_reboot_time > /dev/null$' "$BOOTSTRAP_SH" | cut -d: -f1 || true)"
swap_line="$(grep -n '^ensure_swap$' "$BOOTSTRAP_SH" | cut -d: -f1 || true)"
[ -n "$check_line" ] && [ -n "$swap_line" ] && [ "$check_line" -gt "$seam_line" ] && [ "$check_line" -lt "$swap_line" ] \
    || fail 'the missing-time check no longer runs before the first leg, so a box without a time stops halfway'
pass 'bootstrap.sh checks the time before the first leg and calls the leg below the source seam'

printf '\n%d assertions passed.\n' "$cases"
