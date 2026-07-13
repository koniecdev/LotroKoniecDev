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
