#!/usr/bin/env bash
# One-shot, idempotent bootstrap of a fresh Hetzner VPS (Ubuntu LTS, 24.04 baseline; the live
# CX23 pair runs 26.04, amd64) into the hardened single-node Docker host of ADR-0034 (epic #486,
# ticket #487). Safe to re-run at any time — including on the Phase-0 boxes that were hardened by
# hand: a pass on an already-bootstrapped box converges it and changes nothing on the second run.
#
# Runs ON the server as root. REBOOT_TIME is 01:30 on staging and 02:30 on prod (#1327):
#   scp scripts/hetzner/bootstrap.sh root@<ip>:/root/ && ssh root@<ip> REBOOT_TIME=<time> bash /root/bootstrap.sh
#
# What it sets up:
#   * a 2 GiB /swapfile, persisted in /etc/fstab, + vm.swappiness=10 — the Hetzner images ship with
#     no swap, so a memory spike on a 4 GB box is an instant OOM-kill instead of a slowdown (#708)
#   * Docker Engine + compose plugin — ADOPTS whatever working engine the box already has, and
#     installs docker-ce from Docker's apt repo only when there is none (see the leg below)
#   * ufw: deny incoming except 22/80/443, allow outgoing
#   * a DOCKER-USER firewall rule (rules script + oneshot unit, re-applied whenever docker starts):
#     from the public interface only 80/443 reach a container — Docker's DNAT bypasses ufw (#808)
#   * fail2ban with the systemd backend (24.04 ships no /var/log/auth.log without rsyslog)
#   * unattended-upgrades (security patches apply themselves), plus a night reboot when an update
#     needs one, at REBOOT_TIME (UTC; required on a box's first run — #1327)
#   * non-root `deploy` user: docker group, ssh key auth only (key copied from root), locked
#     password; owns /opt/lotro and /opt/tks (the two compose stacks — ADR-0034 §5)
#   * sshd hardening: PasswordAuthentication no (root stays key-only via Ubuntu's default
#     prohibit-password)
#
# It writes no registry login for `deploy`: every image of our stack is public, and deploy.sh ignores
# the user's Docker config (#1330), so our images must stay public. Another stack with private images
# sets up its own login.
#
# Deliberately NO .ps1 twin: the script-twins rule covers cross-platform dev machines; this runs
# only on the Linux server. Operator docs: docs/deployment/runbook.md.

set -euo pipefail

export DEBIAN_FRONTEND=noninteractive
# Hetzner's Ubuntu preinstalls needrestart, whose TUI would block unattended apt runs.
export NEEDRESTART_MODE=a

log() { printf '\n==> %s\n' "$*"; }

# Writes stdin to $1 with mode $2 only when the content differs; returns 1 when nothing changed,
# so callers can gate service reloads behind `if write_file ...; then`. A failed write exits the
# script outright — callers run in if-context, where set -e is suspended and a plain error would
# silently skip the hardening step.
write_file() {
    local path="$1" mode="$2" tmp
    tmp="$(mktemp)"
    cat > "$tmp"
    if [ -f "$path" ] && cmp -s "$tmp" "$path"; then
        rm -f "$tmp"
        return 1
    fi
    if ! install -m "$mode" "$tmp" "$path"; then
        rm -f "$tmp"
        echo "FATAL: could not write $path" >&2
        exit 1
    fi
    rm -f "$tmp"
}

# Gives the box the shock absorber its image ships without (#708). With no swap a memory spike is
# not a slowdown: the kernel OOM-kills a container and picks the victim by score, not by importance.
# Swappiness stays low because this is an emergency buffer, not a working tier — reach for it under
# real pressure, never page out a warm container just to grow the page cache.
#
# Every step is guarded, so a second pass on a live box changes nothing. Two guards are there for
# safety rather than for idempotence, because this leg edits state that can break a boot: mkswap
# never runs on an area the kernel is using, and only an ACTIVE swapfile is written to fstab — a
# line pointing at a file that is not a swap area fails every `swapon -a` from the next boot on.
#
# The paths and the size are variables so scripts/tests/hetzner-bootstrap-swap.tests.sh can drive
# this function against a temp root with stubbed swap tools. Nothing sets them on a real run.
ensure_swap() {
    local swapfile="${SWAPFILE:-/swapfile}"
    local size_mb="${SWAP_SIZE_MB:-2048}"
    local fstab="${FSTAB:-/etc/fstab}"
    local swappiness_conf="${SWAPPINESS_CONF:-/etc/sysctl.d/99-swappiness.conf}"
    local want_bytes=$((size_mb * 1024 * 1024))

    if [ -n "$(swapon --show=NAME --noheadings 2> /dev/null)" ]; then
        echo "Swap is already active — not adding another one:"
        swapon --show
    else
        # A file of the wrong size is a leftover from a run that died mid-write (a full disk is the
        # way to get one). Adopting it would hand the box a buffer far smaller than it asked for and
        # still report success, so re-create it instead. Nothing is active here, so nothing is lost.
        if [ -e "$swapfile" ] && [ "$(wc -c < "$swapfile")" -ne "$want_bytes" ]; then
            echo "$swapfile is not ${size_mb} MiB — replacing the leftover."
            rm -f "$swapfile"
        fi

        local created=0
        if [ ! -e "$swapfile" ]; then
            echo "No swap on this box — creating $swapfile (${size_mb} MiB)."
            # fallocate is instant on ext4, what the Hetzner image lays down. dd is the fallback for
            # a filesystem that cannot fallocate at all; a failed write leaves a partial file, so it
            # is removed on the way out rather than left for the next run to puzzle over.
            if ! fallocate -l "${size_mb}M" "$swapfile" 2> /dev/null; then
                echo "fallocate failed — writing the file with dd instead."
                rm -f "$swapfile"
                if ! dd if=/dev/zero of="$swapfile" bs=1M count="$size_mb" status=none; then
                    rm -f "$swapfile"
                    echo "FATAL: could not write $swapfile — is the disk full?" >&2
                    exit 1
                fi
            fi
            created=1
        fi
        chmod 600 "$swapfile"   # before mkswap, which complains about a world-readable file

        # mkswap over an active swap area corrupts the pages the kernel holds there. It runs on a
        # file this pass just created, or on one swapon refuses — an interrupted earlier pass leaves
        # a full-size file with no swap signature. swapon can also refuse an ACTIVE file (EBUSY), so
        # that case is checked outright instead of being left to the branch above to rule out.
        if [ "$created" -eq 1 ] || ! swapon "$swapfile" 2> /dev/null; then
            if swapon --show=NAME --noheadings 2> /dev/null | grep -qxF "$swapfile"; then
                echo "FATAL: $swapfile is in use but swapon refused it — refusing to mkswap it." >&2
                exit 1
            fi
            mkswap "$swapfile" > /dev/null
            swapon "$swapfile"
        fi
        swapon --show
    fi

    # Also converges a swapfile that was already active when this ran: the branch above never
    # touched it, and a world-readable swapfile is a copy of memory paged out of other processes.
    if [ -e "$swapfile" ]; then
        chmod 600 "$swapfile"
    fi

    # Persist whatever is active, whoever enabled it — a swapfile someone turned on by hand is one
    # reboot away from being gone. Keyed on ACTIVE, not on "the file exists": a box whose swap comes
    # from a partition may still have a stale /swapfile lying around, and an fstab line for that file
    # would break `swapon -a` at every boot. awk compares the first field exactly, so a path with a
    # regex metacharacter in it cannot loosen the match.
    if swapon --show=NAME --noheadings 2> /dev/null | grep -qxF "$swapfile" \
        && ! awk -v f="$swapfile" '$1 == f { found = 1 } END { exit !found }' "$fstab" 2> /dev/null; then
        echo "Persisting $swapfile in $fstab."
        # An fstab whose last line has no newline would swallow the entry into it, and a malformed
        # root entry is a box that does not boot.
        if [ -s "$fstab" ] && [ -n "$(tail -c 1 "$fstab")" ]; then
            printf '\n' >> "$fstab"
        fi
        printf '%s none swap sw 0 0\n' "$swapfile" >> "$fstab"
        # Warn, never fail: a pre-existing complaint elsewhere in fstab is not this leg's to fix, but
        # nobody should learn about an unparsable fstab at the next reboot.
        findmnt --verify --fstab > /dev/null 2>&1 \
            || echo "WARNING: $fstab does not verify cleanly — check it before rebooting." >&2
        # systemd builds its .swap units from fstab at boot, so the reboot is covered either way —
        # this only stops the RUNNING systemd from disagreeing with the file until someone reloads it.
        if command -v systemctl > /dev/null 2>&1; then
            systemctl daemon-reload
        fi
    fi

    if write_file "$swappiness_conf" 0644 << EOF
# Swap on this box is an emergency buffer, not a working tier (#708).
vm.swappiness = 10
EOF
    then
        sysctl -q -p "$swappiness_conf"
    fi
}

# Closes the gap ufw leaves open (#808). A container port published on 0.0.0.0 answers on the public
# IP whatever ufw says: Docker rewrites the packet's destination (DNAT) before ufw's INPUT rules see
# it, so the packet takes the FORWARD path, where DOCKER-USER is the one chain Docker runs first and
# never rewrites. Only Caddy is supposed to publish (80/443 — the note on the ufw leg); this leg makes
# that true for everything else. It was written for TheKittySaver's CI runner on the staging box
# (koniecdev/TheKittySaver#831, its ADR-0047): Testcontainers publishes Postgres, MinIO and Ryuk —
# which removes containers on a plain TCP command, no auth — on 0.0.0.0 at random ports on every run.
#
# Docker keeps DOCKER-USER across its own restarts, but nothing keeps it across a reboot, so a oneshot
# unit runs the rules script every time docker.service starts, and retries it if that run fails —
# the rules live only in the kernel, so a failed start is an open box. The script replaces the chain
# in one iptables-restore transaction (a declared chain is flushed even under --noflush), so a re-run
# converges instead of stacking duplicates, and a rule that fails to parse changes nothing.
#
# Every pass re-applies the rules, even with both files unchanged. A hand edit while debugging
# (`iptables -I DOCKER-USER 1 -j ACCEPT`, a flush) leaves the unit `active` and the box open; re-running
# this leg is the documented fix, so it has to converge the live chain, not just the files. The
# replace is atomic and its content identical, so on a box that never drifted nothing changes.
#
# The paths are variables so scripts/tests/hetzner-bootstrap-docker-user.tests.sh can drive this
# function against a temp root with stubbed tools, the same way the swap suite drives ensure_swap.
ensure_docker_user_firewall() {
    local rules_script="${DOCKER_USER_SCRIPT:-/usr/local/sbin/docker-user-firewall}"
    local unit_file="${DOCKER_USER_UNIT:-/etc/systemd/system/docker-user-firewall.service}"
    local unit_name changed=0
    unit_name="$(basename "$unit_file")"

    if write_file "$rules_script" 0755 << 'EOF'
#!/usr/bin/env bash
# Written by LotroKoniecDev scripts/hetzner/bootstrap.sh (#808) — re-run its docker-user leg to
# change it; edits here are overwritten. docker-user-firewall.service runs it when docker starts.
#
# From the public interface, only ports 80 and 443 reach a container. The rules match the ORIGINAL
# destination port through conntrack, because DNAT has already rewritten the port by the time a
# packet reaches DOCKER-USER (Docker docs, "Packet filtering and firewalls").
set -euo pipefail

# awk reads to the end: one that stopped at the first default route could kill `ip` with SIGPIPE,
# and pipefail would then stop this script (#1365).
ext_if="$(ip -o route show default | awk '!found { for (i = 1; i < NF; i++) if ($i == "dev") { print $(i + 1); found = 1; break } }')"
if [ -z "$ext_if" ]; then
    echo "docker-user-firewall: no default route, so the public interface is unknown — nothing applied." >&2
    exit 1
fi

rules() {
    printf '%s\n' \
        '*filter' \
        ':DOCKER-USER - [0:0]' \
        "-A DOCKER-USER -i ${ext_if} -m conntrack --ctstate RELATED,ESTABLISHED -j RETURN" \
        "-A DOCKER-USER -i ${ext_if} -p tcp -m conntrack --ctdir ORIGINAL --ctorigdstport 80 -j RETURN" \
        "-A DOCKER-USER -i ${ext_if} -p tcp -m conntrack --ctdir ORIGINAL --ctorigdstport 443 -j RETURN" \
        "-A DOCKER-USER -i ${ext_if} -j DROP" \
        'COMMIT'
}

# Docker creates the chain and the jump to it from FORWARD. Without the jump these rules would sit
# where no packet goes and protect nothing, so a missing IPv4 jump fails instead of passing quietly.
if ! iptables -w -C FORWARD -j DOCKER-USER > /dev/null 2>&1; then
    echo "docker-user-firewall: no IPv4 FORWARD jump to DOCKER-USER — Docker's iptables integration is off, so nothing filters published ports. Nothing applied." >&2
    exit 1
fi
rules | iptables-restore -w --noflush
echo "docker-user-firewall: IPv4 — only 80/443 reach a container from ${ext_if}."

# Docker filters IPv6 only when it manages ip6tables (the default since Docker 27). Without that
# jump an IPv6 published port is served by docker-proxy on the host, where ufw's INPUT rules apply.
if ip6tables -w -C FORWARD -j DOCKER-USER > /dev/null 2>&1; then
    rules | ip6tables-restore -w --noflush
    echo "docker-user-firewall: IPv6 — only 80/443 reach a container from ${ext_if}."
else
    echo "docker-user-firewall: IPv6 — no FORWARD jump to DOCKER-USER; docker-proxy serves IPv6 ports behind ufw. Skipped."
fi
EOF
    then
        changed=1
    fi

    if write_file "$unit_file" 0644 << EOF
# Written by LotroKoniecDev scripts/hetzner/bootstrap.sh (#808) — edits here are overwritten.
[Unit]
Description=DOCKER-USER firewall: from the public interface only 80/443 reach a container (#808)
After=docker.service network-online.target
Wants=network-online.target
PartOf=docker.service

[Service]
Type=oneshot
RemainAfterExit=yes
ExecStart=${rules_script}
Restart=on-failure
RestartSec=5s

[Install]
WantedBy=docker.service
EOF
    then
        changed=1
    fi

    if [ "$changed" -eq 1 ]; then
        systemctl daemon-reload
    fi
    systemctl enable --quiet "$unit_name"
    # restart, not start: `start` on an active RemainAfterExit unit is a no-op and would leave a
    # hand-edited chain in place (see the comment above the function).
    systemctl restart "$unit_name"
}

# unattended-upgrades installs a new kernel, libc or Docker but never reboots into it on its own
# (#1327). This file makes it reboot at a fixed UTC time on the nights an update asks for one.
# Staging and prod need different times (runbook, Gotchas), so there is no default.
ensure_unattended_reboot() {
    local conf="${UNATTENDED_REBOOT_CONF:-/etc/apt/apt.conf.d/52unattended-reboot}"
    local reboot_time
    reboot_time="$(unattended_reboot_time)" || exit 1

    write_file "$conf" 0644 << EOF || true
// Written by LotroKoniecDev scripts/hetzner/bootstrap.sh (#1327); edits here are overwritten.
// Set the time with REBOOT_TIME=HH:MM (UTC) on a bootstrap run.
Unattended-Upgrade::Automatic-Reboot "true";
Unattended-Upgrade::Automatic-Reboot-Time "${reboot_time}";
EOF
    echo "unattended-upgrades reboots this box at ${reboot_time} UTC when an update needs it."
}

# Prints the box's reboot time: REBOOT_TIME when set, else the time a previous run wrote. A box with
# neither stops here, so a re-run can never give staging prod's time by default.
unattended_reboot_time() {
    local conf="${UNATTENDED_REBOOT_CONF:-/etc/apt/apt.conf.d/52unattended-reboot}"
    local reboot_time="${REBOOT_TIME:-}"

    if [ -z "$reboot_time" ] && [ -f "$conf" ]; then
        reboot_time="$(sed -n 's/^Unattended-Upgrade::Automatic-Reboot-Time "\(.*\)";$/\1/p' "$conf")"
    fi
    if [ -z "$reboot_time" ]; then
        echo "FATAL: this box has no reboot time yet. Run with REBOOT_TIME=01:30 on staging or REBOOT_TIME=02:30 on prod (UTC)." >&2
        exit 1
    fi
    # Digit lists, not ranges: under a UTF-8 locale [0-9] also matches digits such as ² (#1162).
    if ! [[ "$reboot_time" =~ ^([01][0123456789]|2[0123]):[012345][0123456789]$ ]]; then
        echo "FATAL: the reboot time must be HH:MM in UTC (for example 01:30), not '${reboot_time}'." >&2
        exit 1
    fi
    printf '%s\n' "$reboot_time"
}

# Test seam: `BOOTSTRAP_SOURCE_ONLY=1 . bootstrap.sh` defines the helpers above and stops here,
# before the script touches anything. It is what lets the self-tests drive ensure_swap,
# ensure_docker_user_firewall and ensure_unattended_reboot against a temp root, and how an
# already-bootstrapped box gets one leg without a full re-run (#708, #808, #1327).
if [ "${BOOTSTRAP_SOURCE_ONLY:-0}" = "1" ]; then
    # `return` is the sourced path; the fallback catches someone EXECUTING the script with the seam
    # set, where a bare `return` would be an error. SC2317: not dead code, just conditionally reached.
    # shellcheck disable=SC2317
    return 0 2> /dev/null || exit 0
fi

if [ "$(id -u)" -ne 0 ]; then
    echo "bootstrap.sh must run as root (fresh Hetzner boxes ssh you in as root)." >&2
    exit 1
fi

# shellcheck disable=SC1091
CODENAME="$(. /etc/os-release && echo "${VERSION_CODENAME:-}")"
if [ -z "$CODENAME" ]; then
    echo "WARNING: could not detect the Ubuntu codename — using 'noble' (24.04) for the Docker apt repo." >&2
    CODENAME="noble"
fi
echo "Detected Ubuntu codename: $CODENAME"

# Stop before the first leg, not halfway through, when the box has no reboot time yet (#1327).
unattended_reboot_time > /dev/null

# First leg on purpose: every step below it (apt, the image pulls a later deploy runs) is a memory
# spike on a box that has no buffer until this returns.
log "swap (2 GiB emergency buffer + vm.swappiness=10)"
ensure_swap

log "apt update + base packages"
apt-get update -q
# --no-upgrade everywhere: install what is missing, never upgrade in place — a re-run on a live
# box must not bounce daemons (a docker-ce upgrade restarts dockerd and every prod container).
# Upgrades are unattended-upgrades' job (security) or a deliberate operator action.
apt-get install -qy --no-upgrade ca-certificates curl ufw fail2ban unattended-upgrades python3-systemd

log "Docker Engine + compose plugin"
# ADOPT an engine that is already there; install docker-ce ONLY on a box that has none.
#
# The live CX23 pair (and any box provisioned from Ubuntu's own archive) runs docker.io +
# containerd + docker-compose-v2. Those packages CONFLICT with Docker's docker-ce stack, so
# `apt-get install docker-ce containerd.io …` on such a box is not an install — it is an engine
# SWAP: apt removes the running docker.io/containerd and dockerd restarts, bouncing every container
# on the host (BOTH compose stacks — /opt/lotro and /opt/tks). `--no-upgrade` does not save us
# here; it only skips packages that are already installed, and docker-ce is not one of them.
#
# The script needs an engine and the compose plugin — not a specific vendor's packaging of them.
# So: probe for the capability, and touch apt only when the capability is missing. This is what
# makes the "safe to re-run on the hand-hardened Phase-0 boxes" promise in the header actually true.
# Found the hard way on 2026-07-13, wiring CD to the live pair (#491 follow-up).
if command -v docker > /dev/null 2>&1 && docker compose version > /dev/null 2>&1; then
    echo "Engine + compose plugin already present ($(docker --version)) — adopting it."
    echo "NOT installing docker-ce: it conflicts with Ubuntu's docker.io and the swap would restart every container."
else
    echo "No Docker engine (or no compose plugin) — installing docker-ce from Docker's apt repo."
    install -m 0755 -d /etc/apt/keyrings
    if [ ! -f /etc/apt/keyrings/docker.asc ]; then
        curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
        chmod a+r /etc/apt/keyrings/docker.asc
    fi
    if write_file /etc/apt/sources.list.d/docker.list 0644 <<EOF
deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu ${CODENAME} stable
EOF
    then
        apt-get update -q
    fi
    apt-get install -qy --no-upgrade docker-ce docker-ce-cli containerd.io docker-compose-plugin
fi
systemctl enable --now docker

log "deploy user (docker group, key-only ssh, locked password)"
if ! id deploy > /dev/null 2>&1; then
    useradd --create-home --shell /bin/bash deploy
fi
usermod -aG docker deploy
# Guarded: repeating `passwd -l` on a locked account still rewrites /etc/shadow.
if [ "$(passwd -S deploy | awk '{ print $2 }')" != "L" ]; then
    passwd -l deploy > /dev/null
fi
install -d -m 0700 -o deploy -g deploy /home/deploy/.ssh
if [ ! -s /home/deploy/.ssh/authorized_keys ]; then
    if [ -s /root/.ssh/authorized_keys ]; then
        install -m 0600 -o deploy -g deploy /root/.ssh/authorized_keys /home/deploy/.ssh/authorized_keys
    else
        echo "WARNING: /root/.ssh/authorized_keys is empty — add a key to /home/deploy/.ssh/authorized_keys manually." >&2
    fi
fi

log "stack directories (/opt/lotro for LotroKoniecDev, /opt/tks for TheKittySaver — ADR-0034 §5)"
# -R, not just `install -d`: on a box where the stack was landed BEFORE the deploy user existed (the
# Phase-0 pair — root scp'd the files in by hand), `install -d` chowns only the directory and leaves
# every file inside it root-owned. Bootstrap then reports success while CD still cannot deploy: `.env`
# is 0600 root, so deploy cannot read it (no compose run at all), and scp of compose.hetzner.yaml /
# the Caddyfile / deploy.sh cannot truncate a root-owned file in a deploy-owned directory. Converging
# the CONTENTS is what makes the deploy user real. Modes are left alone — only ownership moves.
install -d -m 0755 -o deploy -g deploy /opt/lotro /opt/tks
chown -R deploy:deploy /opt/lotro /opt/tks

log "sshd hardening (PasswordAuthentication no)"
# Lockout guard: never disable password auth on a box where NO key-based path exists (a box
# provisioned via Hetzner's password e-mail has an empty /root/.ssh/authorized_keys) — the
# current session would be the last one to ever get in.
if [ ! -s /root/.ssh/authorized_keys ] && [ ! -s /home/deploy/.ssh/authorized_keys ]; then
    echo "FATAL: no authorized_keys for root or deploy — refusing to disable ssh password auth." >&2
    echo "       Add a public key (e.g. to /root/.ssh/authorized_keys), then re-run." >&2
    exit 1
fi
# sshd honours the FIRST occurrence of a keyword, and /etc/ssh/sshd_config.d is included at the
# very top of sshd_config in lexical order — so 00- makes these values win over cloud-init's
# 50-cloud-init.conf regardless of what the image baked in. Same filename as the hand-written
# Phase-0 hardening on the live pair, so this converges that file instead of shadowing it.
install -d -m 0755 /etc/ssh/sshd_config.d
if write_file /etc/ssh/sshd_config.d/00-hardening.conf 0644 <<'EOF'
PasswordAuthentication no
KbdInteractiveAuthentication no
PermitRootLogin prohibit-password
EOF
then
    sshd -t
    systemctl reload ssh
fi

log "ufw (deny incoming; allow 22/80/443)"
# Note: Docker's iptables rules bypass ufw for PUBLISHED container ports. Only Caddy publishes
# ports in our stacks (80/443, allowed below anyway) — the DOCKER-USER leg right after this one
# enforces it; see the runbook gotchas.
ufw default deny incoming
ufw default allow outgoing
ufw allow 22/tcp
ufw allow 80/tcp
ufw allow 443/tcp
ufw --force enable

log "DOCKER-USER firewall (from the public interface only 80/443 reach a container)"
ensure_docker_user_firewall

log "fail2ban (sshd jail on the systemd backend)"
if write_file /etc/fail2ban/jail.local 0644 <<'EOF'
# Ubuntu 24.04 has no /var/log/auth.log out of the box (no rsyslog), so the default sshd jail
# cannot start — read sshd auth failures from the systemd journal instead.
[sshd]
enabled = true
backend = systemd
EOF
then
    systemctl restart fail2ban
fi
systemctl enable --now fail2ban

log "unattended-upgrades (+ a night reboot when an update needs one)"
write_file /etc/apt/apt.conf.d/20auto-upgrades 0644 <<'EOF' || true
APT::Periodic::Update-Package-Lists "1";
APT::Periodic::Unattended-Upgrade "1";
EOF
ensure_unattended_reboot
systemctl enable --now unattended-upgrades

log "verification"
# deploy.sh runs docker without the user's Docker config, so compose must work without it too (#1330).
su - deploy -c 'DOCKER_CONFIG=/nonexistent docker compose version'
# Proves the docker-group membership end-to-end, not just that the CLI binary exists.
su - deploy -c 'docker ps > /dev/null'
echo "deploy can reach the Docker daemon"
ufw status verbose
if ! systemctl is-active --quiet docker fail2ban unattended-upgrades docker-user-firewall; then
    echo "FATAL: docker/fail2ban/unattended-upgrades/docker-user-firewall not all active — inspect with systemctl status." >&2
    exit 1
fi
echo "docker + fail2ban + unattended-upgrades + docker-user-firewall: active"
echo
echo "Bootstrap complete. Next: land the stack files in /opt/lotro and bring it up"
echo "(docs/deployment/runbook.md)."
