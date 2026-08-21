#!/usr/bin/env bash
# One-shot, idempotent bootstrap of a fresh Hetzner VPS (Ubuntu LTS, 24.04 baseline; the live
# CX23 pair runs 26.04, amd64) into the hardened single-node Docker host of ADR-0034 (epic #486,
# ticket #487). Safe to re-run at any time — including on the Phase-0 boxes that were hardened by
# hand: a pass on an already-bootstrapped box converges it and changes nothing on the second run.
#
# Runs ON the server as root. Preferred invocation (the -t gives the GHCR prompt its TTY, so the
# PAT never touches shell history or argv):
#   scp scripts/hetzner/bootstrap.sh root@<ip>:/root/ && ssh -t root@<ip> bash /root/bootstrap.sh
# Non-interactive/automation form — NOTE: the inline env assignments land in YOUR shell history:
#   ssh root@<ip> 'GHCR_USER=<user> GHCR_TOKEN=<pat> bash -s' < scripts/hetzner/bootstrap.sh
#
# What it sets up:
#   * a 2 GiB /swapfile, persisted in /etc/fstab, + vm.swappiness=10 — the Hetzner images ship with
#     no swap, so a memory spike on a 4 GB box is an instant OOM-kill instead of a slowdown (#708)
#   * Docker Engine + compose plugin — ADOPTS whatever working engine the box already has, and
#     installs docker-ce from Docker's apt repo only when there is none (see the leg below)
#   * ufw: deny incoming except 22/80/443, allow outgoing
#   * fail2ban with the systemd backend (24.04 ships no /var/log/auth.log without rsyslog)
#   * unattended-upgrades (security patches apply themselves)
#   * non-root `deploy` user: docker group, ssh key auth only (key copied from root), locked
#     password; owns /opt/lotro and /opt/tks (the two compose stacks — ADR-0034 §5)
#   * sshd hardening: PasswordAuthentication no (root stays key-only via Ubuntu's default
#     prohibit-password)
#   * `docker login ghcr.io` as deploy with a READ-ONLY (read:packages) PAT taken from
#     GHCR_USER/GHCR_TOKEN env or an interactive prompt — never stored in this script or the repo
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
    fi

    if write_file "$swappiness_conf" 0644 << EOF
# Swap on this box is an emergency buffer, not a working tier (#708).
vm.swappiness = 10
EOF
    then
        sysctl -q -p "$swappiness_conf"
    fi
}

# Test seam: `BOOTSTRAP_SOURCE_ONLY=1 . bootstrap.sh` defines the helpers above and stops here,
# before the script touches anything. It is what lets the swap self-test drive ensure_swap against a
# temp root, and how an already-bootstrapped box gets this one leg without a full re-run (#708).
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
# ports in our stacks (80/443, allowed below anyway) — keep it that way; see the runbook gotchas.
ufw default deny incoming
ufw default allow outgoing
ufw allow 22/tcp
ufw allow 80/tcp
ufw allow 443/tcp
ufw --force enable

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

log "unattended-upgrades"
write_file /etc/apt/apt.conf.d/20auto-upgrades 0644 <<'EOF' || true
APT::Periodic::Update-Package-Lists "1";
APT::Periodic::Unattended-Upgrade "1";
EOF
systemctl enable --now unattended-upgrades

log "GHCR login as deploy (read-only PAT)"
ghcr_logged_in() {
    grep -q '"ghcr.io"' /home/deploy/.docker/config.json 2> /dev/null
}
ghcr_login() {
    export GHCR_USER
    # Single quotes on purpose (SC2016): $GHCR_USER must expand inside deploy's login shell,
    # where su's -w whitelist delivers it — the token itself only ever travels via stdin.
    # shellcheck disable=SC2016
    printf '%s' "$GHCR_TOKEN" | su - deploy -w GHCR_USER -c 'docker login ghcr.io -u "$GHCR_USER" --password-stdin'
}
if [ -n "${GHCR_USER:-}" ] && [ -n "${GHCR_TOKEN:-}" ]; then
    ghcr_login
elif ghcr_logged_in; then
    echo "deploy is already logged in to ghcr.io — skipping (set GHCR_USER/GHCR_TOKEN to refresh)."
elif [ -t 0 ]; then
    read -rp "GitHub username for ghcr.io: " GHCR_USER
    read -rsp "GHCR read-only PAT (read:packages): " GHCR_TOKEN
    echo
    ghcr_login
else
    echo "WARNING: no GHCR_USER/GHCR_TOKEN in env, no TTY, and deploy is not logged in to ghcr.io yet." >&2
    echo "         Re-run with: GHCR_USER=<user> GHCR_TOKEN=<read-only pat> bash bootstrap.sh" >&2
fi

log "verification"
su - deploy -c 'docker compose version'
# Proves the docker-group membership end-to-end, not just that the CLI binary exists.
su - deploy -c 'docker ps > /dev/null'
echo "deploy can reach the Docker daemon"
ufw status verbose
if ! systemctl is-active --quiet docker fail2ban unattended-upgrades; then
    echo "FATAL: docker/fail2ban/unattended-upgrades not all active — inspect with systemctl status." >&2
    exit 1
fi
echo "docker + fail2ban + unattended-upgrades: active"
echo
echo "Bootstrap complete. Next: land the stack files in /opt/lotro and bring it up"
echo "(docs/deployment/runbook.md)."
