#!/usr/bin/env bash
# Test suite for the DOCKER-USER leg of the Hetzner bootstrap (scripts/hetzner/bootstrap.sh — #808).
#
# The leg installs two files on the box — a rules script and a oneshot unit that runs it whenever
# docker.service starts — and the rules script is what stands between the public internet and every
# container port published on 0.0.0.0. Both halves are proven here the way the swap leg is: the tools
# they shell out to (systemctl, ip, iptables, ip6tables and their -restore twins) are STUBS first on
# PATH that record what they were asked, and the paths the function writes are pointed at a throwaway
# root through its own variables. The rules script is then EXECUTED against the same stubs, so the
# rules asserted below are the ones the box would load, not a copy of them.
#
# The properties that matter: only 80/443 pass from the public interface, the rules match the port
# BEFORE Docker's DNAT rewrote it, the load never flushes the whole filter table (that would wipe ufw
# and Docker's own rules), nothing touches the chain outside that one transaction, a box without the
# FORWARD jump or with a failing load fails instead of reporting a filter that filters nothing, and a
# second pass re-applies the live chain without reloading systemd.
#
# pr-verify and CI run this in their `build` job (the classifier's `guards` verdict), right after
# the swap suite.

set -euo pipefail

# With CDPATH set, `cd` to a relative path may print the folder, and that text would end up here (#1048).
SCRIPTS_DIR="$(CDPATH='' cd -- "$(dirname "$0")/.." && pwd)"
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
    printf '  --- last output ---\n%s\n' "$LAST_OUTPUT" | sed 's/^/    /'
    exit 1
}

pass() {
    cases=$((cases + 1))
    printf '✓ [%s] %s\n' "$CASE" "$1"
}

# One throwaway "box": a fake root plus stubs for every tool the leg and its rules script shell out
# to. The box state the stubs read lives in files: `default-route` is what `ip -o route show default`
# prints, `chain-v4` / `chain-v6` exist when Docker jumps from FORWARD to DOCKER-USER for that family,
# and `restore-fails-v4` / `-v6` make that family's iptables-restore fail.
new_box() {
    # Exported: the stubs are child processes and read $BOX to find their state and call log.
    export BOX="$TMP_ROOT/box-$((++box_seq))"
    mkdir -p "$BOX/bin"
    : > "$BOX/calls.log"
    printf '%s\n' 'default via 172.31.1.1 dev eth0 proto dhcp src 91.98.74.228 metric 100' > "$BOX/default-route"
    : > "$BOX/chain-v4"
    : > "$BOX/chain-v6"

    cat > "$BOX/bin/systemctl" <<'EOF'
#!/usr/bin/env bash
echo "systemctl $*" >> "$BOX/calls.log"
EOF

    # Answers only the one question the script should ask — the main table's default route.
    cat > "$BOX/bin/ip" <<'EOF'
#!/usr/bin/env bash
echo "ip $*" >> "$BOX/calls.log"
[ "$*" = '-o route show default' ] || exit 1
cat "$BOX/default-route"
EOF

    # iptables/ip6tables: the FORWARD jump probe answers from the box state; every call is recorded,
    # so a rule changed outside the restore transaction shows up in the call log.
    for family in v4 v6; do
        tool=iptables
        [ "$family" = v6 ] && tool=ip6tables
        cat > "$BOX/bin/$tool" <<EOF
#!/usr/bin/env bash
echo "$tool \$*" >> "\$BOX/calls.log"
if [ "\$*" = "-w -C FORWARD -j DOCKER-USER" ]; then
    [ -f "\$BOX/chain-$family" ]
    exit
fi
EOF
        cat > "$BOX/bin/$tool-restore" <<EOF
#!/usr/bin/env bash
echo "$tool-restore \$*" >> "\$BOX/calls.log"
cat > "\$BOX/restore-$family.txt"
if [ -f "\$BOX/restore-fails-$family" ]; then
    echo "$tool-restore: line 4 failed" >&2
    exit 1
fi
EOF
    done

    chmod +x "$BOX"/bin/*
}

# Drives the real ensure_docker_user_firewall out of the real bootstrap.sh, through its source seam.
run_leg() {
    LAST_STATUS=0
    LAST_OUTPUT="$(
        PATH="$BOX/bin:$PATH" \
        BOOTSTRAP_SOURCE_ONLY=1 \
        DOCKER_USER_SCRIPT="$BOX/docker-user-firewall" \
        DOCKER_USER_UNIT="$BOX/docker-user-firewall.service" \
        bash -c '. "$0" && ensure_docker_user_firewall' "$BOOTSTRAP_SH" 2>&1
    )" || LAST_STATUS=$?
    [ "$LAST_STATUS" -eq 0 ] || fail "ensure_docker_user_firewall exited $LAST_STATUS" "$LAST_OUTPUT"
}

# Runs the rules script the leg wrote — the file the unit's ExecStart points at.
run_rules() {
    LAST_STATUS=0
    LAST_OUTPUT="$(PATH="$BOX/bin:$PATH" "$BOX/docker-user-firewall" 2>&1)" || LAST_STATUS=$?
    if [ "${EXPECT_FAILURE:-0}" = 1 ]; then
        [ "$LAST_STATUS" -ne 0 ] || fail 'the rules script reported success where it had to fail' "$LAST_OUTPUT"
    else
        [ "$LAST_STATUS" -eq 0 ] || fail "the rules script exited $LAST_STATUS" "$LAST_OUTPUT"
    fi
}

calls_matching() { grep -cE "$1" "$BOX/calls.log" || true; }
# GNU stat FIRST: `stat -f` means "filesystem status" on coreutils (same note as the swap suite).
file_mode() { stat -c '%a' "$1" 2> /dev/null || stat -f '%Lp' "$1"; }

expected_rules() {
    printf '%s\n' \
        '*filter' \
        ':DOCKER-USER - [0:0]' \
        "-A DOCKER-USER -i $1 -m conntrack --ctstate RELATED,ESTABLISHED -j RETURN" \
        "-A DOCKER-USER -i $1 -p tcp -m conntrack --ctdir ORIGINAL --ctorigdstport 80 -j RETURN" \
        "-A DOCKER-USER -i $1 -p tcp -m conntrack --ctdir ORIGINAL --ctorigdstport 443 -j RETURN" \
        "-A DOCKER-USER -i $1 -j DROP" \
        'COMMIT'
}

box_seq=0

# ---------------------------------------------------------------------------------------------
CASE='fresh box'
new_box
run_leg

[ -f "$BOX/docker-user-firewall" ] || fail 'no rules script was written'
[ "$(file_mode "$BOX/docker-user-firewall")" = '755' ] || fail 'the rules script is not executable (755)'
[ "$(file_mode "$BOX/docker-user-firewall.service")" = '644' ] || fail 'the unit is not 644'
pass 'writes the rules script (755) and the unit (644)'

unit="$BOX/docker-user-firewall.service"
grep -qxF "ExecStart=$BOX/docker-user-firewall" "$unit" || fail 'the unit does not run the rules script it wrote'
grep -qxF 'WantedBy=docker.service' "$unit" || fail 'nothing starts the unit when docker starts — the rules are gone after a reboot'
grep -qxF 'PartOf=docker.service' "$unit" || fail 'a docker restart would not re-apply the rules'
grep -qxF 'After=docker.service network-online.target' "$unit" \
    || fail 'the unit may run before Docker has created DOCKER-USER or before the default route exists'
grep -qxF 'Wants=network-online.target' "$unit" || fail 'nothing pulls in network-online.target — the ordering on it is empty'
grep -qxF 'Type=oneshot' "$unit" || fail 'the unit is not a oneshot'
grep -qxF 'RemainAfterExit=yes' "$unit" \
    || fail 'the unit does not stay active after the script exits — "start" on a re-run would apply the rules again'
pass 'the unit runs the script whenever docker.service starts or restarts'

grep -qxF 'Restart=on-failure' "$unit" \
    || fail 'a failed start is never retried — the rules live only in the kernel, so that box stays open'
grep -qxF 'RestartSec=5s' "$unit" || fail 'the retry delay is not 5 s'
pass 'a failed start is retried every 5 s'

grep -qF 'DOCKER_USER_SCRIPT:-/usr/local/sbin/docker-user-firewall}' "$BOOTSTRAP_SH" \
    || fail 'the real rules-script path drifted from the one the runbook names'
grep -qF 'DOCKER_USER_UNIT:-/etc/systemd/system/docker-user-firewall.service}' "$BOOTSTRAP_SH" \
    || fail 'the real unit path drifted from the one the runbook names'
pass 'on a real box the files land where the runbook says'

[ "$(calls_matching '^systemctl daemon-reload$')" -eq 1 ] || fail 'systemd never learned about the new unit'
[ "$(calls_matching '^systemctl enable --quiet docker-user-firewall\.service$')" -eq 1 ] \
    || fail 'the unit is not enabled — the next reboot leaves the box unfiltered'
[ "$(calls_matching '^systemctl restart docker-user-firewall\.service$')" -eq 1 ] \
    || fail 'the rules were written but never applied'
pass 'reloads systemd, enables the unit and applies the rules now'

# ---------------------------------------------------------------------------------------------
# bootstrap.sh's header promise: a second pass on a live box changes nothing.
CASE='second pass on the same box'
: > "$BOX/calls.log"
run_leg

[ "$(calls_matching '^systemctl daemon-reload$')" -eq 0 ] || fail 'a re-run reloaded systemd for unchanged files'
[ "$(calls_matching '^systemctl restart docker-user-firewall\.service$')" -eq 1 ] \
    || fail 'a re-run left the live chain as it found it — a hand edit would survive the documented fix'
pass 'does not reload systemd, and re-applies the live chain (an atomic replace with the same rules)'

# ---------------------------------------------------------------------------------------------
CASE='rules script edited by hand'
new_box
run_leg
printf '%s\n' '#!/usr/bin/env bash' 'exit 0' > "$BOX/docker-user-firewall"
: > "$BOX/calls.log"
run_leg

grep -q 'iptables-restore -w --noflush' "$BOX/docker-user-firewall" || fail 'the hand-edited script was kept'
[ "$(calls_matching '^systemctl restart docker-user-firewall\.service$')" -eq 1 ] \
    || fail 'the restored script was written but not applied'
pass 'converges it back and re-applies the rules'

# ---------------------------------------------------------------------------------------------
CASE='rules script, both families'
new_box
run_leg
: > "$BOX/calls.log"
run_rules

[ "$(cat "$BOX/restore-v4.txt")" = "$(expected_rules eth0)" ] \
    || fail 'the IPv4 rule set is not exactly established → 80 → 443 → drop' "$(cat "$BOX/restore-v4.txt")"
pass 'IPv4: from eth0 only replies and new connections to 80/443 pass; everything else is dropped'

[ "$(cat "$BOX/restore-v6.txt")" = "$(expected_rules eth0)" ] \
    || fail 'the IPv6 rule set differs from the IPv4 one' "$(cat "$BOX/restore-v6.txt")"
pass 'IPv6: the same rule set'

# Without --noflush iptables-restore flushes the WHOLE filter table first: ufw's chains and Docker's
# own forwarding rules would be gone, and with them every container's network.
[ "$(calls_matching '^iptables-restore -w --noflush$')" -eq 1 ] \
    && [ "$(calls_matching '^ip6tables-restore -w --noflush$')" -eq 1 ] \
    || fail 'a restore ran without --noflush — that wipes ufw and Docker rules'
pass 'loads each family with --noflush, so only DOCKER-USER is replaced'

# An allowlist, not a denylist: whatever the spelling (-I, -t filter -A, --insert), any iptables call
# besides the jump probe is a rule changed outside the transaction — one `-j RETURN` there opens
# every published port again.
others="$(grep -E '^ip6?tables ' "$BOX/calls.log" | grep -vxE 'ip6?tables -w -C FORWARD -j DOCKER-USER' || true)"
[ -z "$others" ] || fail 'iptables was called outside the restore transaction' "$others"
pass 'touches the chain only inside one restore transaction per family'

# DNAT rewrote the destination before DOCKER-USER sees the packet: --dport 443 would match the
# container's port, not the published one, and could let a different published port through.
grep -q -- '--dport' "$BOX/restore-v4.txt" && fail 'a rule matches --dport, which DNAT has already rewritten'
pass 'matches the original destination port (conntrack), not the rewritten one'

# ---------------------------------------------------------------------------------------------
CASE='public interface is not eth0'
new_box
printf '%s\n' 'default via 172.31.1.1 dev enp1s0 proto dhcp src 91.98.74.228 metric 100' > "$BOX/default-route"
run_leg
run_rules

[ "$(cat "$BOX/restore-v4.txt")" = "$(expected_rules enp1s0)" ] \
    || fail 'the rules do not follow the interface of the default route' "$(cat "$BOX/restore-v4.txt")"
pass 'takes the interface from the default route'

# ---------------------------------------------------------------------------------------------
CASE='Docker does not manage ip6tables'
new_box
rm -f "$BOX/chain-v6"
run_leg
run_rules

[ -f "$BOX/restore-v4.txt" ] || fail 'IPv4 was skipped together with IPv6'
[ -e "$BOX/restore-v6.txt" ] && fail 'rules were loaded into an IPv6 chain nothing jumps to'
printf '%s' "$LAST_OUTPUT" | grep -q 'IPv6 — no FORWARD jump to DOCKER-USER' || fail 'the IPv6 skip was silent' "$LAST_OUTPUT"
pass 'applies IPv4, skips IPv6 and says so'

# ---------------------------------------------------------------------------------------------
# A load that fails must fail the unit: "active (exited)" over a chain that never loaded is a box
# that looks protected and is not. Restart=on-failure only retries what reports a failure.
CASE='IPv4 load fails'
new_box
run_leg
: > "$BOX/restore-fails-v4"
EXPECT_FAILURE=1 run_rules
EXPECT_FAILURE=0

printf '%s' "$LAST_OUTPUT" | grep -q 'IPv4 — only 80/443' && fail 'a failed IPv4 load still reported success' "$LAST_OUTPUT"
[ "$(calls_matching '^ip6tables-restore')" -eq 0 ] || fail 'the script carried on after the IPv4 load failed'
pass 'fails without claiming the box is filtered'

CASE='IPv6 load fails'
new_box
run_leg
: > "$BOX/restore-fails-v6"
EXPECT_FAILURE=1 run_rules
EXPECT_FAILURE=0

printf '%s' "$LAST_OUTPUT" | grep -q 'IPv6 — only 80/443' && fail 'a failed IPv6 load still reported success' "$LAST_OUTPUT"
pass 'fails too — a half-filtered box is not reported as done'

# ---------------------------------------------------------------------------------------------
# Rules in a chain nothing jumps to filter nothing. Reporting success there would be the worst
# outcome: a box that looks protected and is not.
CASE='Docker iptables integration off'
new_box
rm -f "$BOX/chain-v4"
run_leg
EXPECT_FAILURE=1 run_rules
EXPECT_FAILURE=0

[ "$(calls_matching '^ip6?tables-restore')" -eq 0 ] || fail 'rules were loaded although nothing jumps to the chain'
printf '%s' "$LAST_OUTPUT" | grep -q 'no IPv4 FORWARD jump to DOCKER-USER' || fail 'the failure does not say why' "$LAST_OUTPUT"
pass 'fails, loads nothing and says why'

# ---------------------------------------------------------------------------------------------
CASE='no default route'
new_box
: > "$BOX/default-route"
run_leg
EXPECT_FAILURE=1 run_rules
EXPECT_FAILURE=0

[ "$(calls_matching '^ip6?tables-restore')" -eq 0 ] || fail 'rules were loaded for an unknown interface'
pass 'fails and loads nothing — an empty interface name would drop every forwarded packet'

# ---------------------------------------------------------------------------------------------
CASE='bootstrap wiring'
# `|| true`: a non-matching grep would trip pipefail and kill the suite before fail() could say why.
call_line="$(grep -n '^ensure_docker_user_firewall$' "$BOOTSTRAP_SH" | cut -d: -f1 || true)"
[ -n "$call_line" ] || fail 'bootstrap.sh never calls ensure_docker_user_firewall — a fresh box stays open'
ufw_line="$(grep -n '^ufw --force enable$' "$BOOTSTRAP_SH" | cut -d: -f1 || true)"
[ -n "$ufw_line" ] && [ "$call_line" -gt "$ufw_line" ] \
    || fail 'the DOCKER-USER leg no longer runs after the ufw leg'
docker_line="$(grep -n '^systemctl enable --now docker$' "$BOOTSTRAP_SH" | cut -d: -f1 || true)"
[ -n "$docker_line" ] && [ "$call_line" -gt "$docker_line" ] \
    || fail 'the DOCKER-USER leg runs before Docker is up — the chain would not exist yet'
pass 'bootstrap.sh calls the leg after Docker is up and ufw is on'

# ---------------------------------------------------------------------------------------------
CASE='source seam'
LAST_STATUS=0
out="$(BOOTSTRAP_SOURCE_ONLY=1 bash -c '. "$0" && declare -F ensure_docker_user_firewall > /dev/null && echo defined' "$BOOTSTRAP_SH" 2>&1)" \
    || LAST_STATUS=$?
[ "$LAST_STATUS" -eq 0 ] && [ "$out" = 'defined' ] \
    || fail 'BOOTSTRAP_SOURCE_ONLY=1 does not define ensure_docker_user_firewall' "$out"
pass 'defines the leg and stops before touching the box — how a live box gets it'

printf '\n%d assertions passed.\n' "$cases"
