#!/usr/bin/env bash
# Docker restore-graph guard.
#
# Every image Dockerfile copies the .csproj files first, runs `dotnet restore`, and only then
# copies the sources — so NuGet restore lands in a layer keyed on the project graph alone and a
# C# edit never re-downloads packages. That optimisation has a silent failure mode:
#
#     dotnet restore  →  "Skipping project '…/Foo.csproj' because it was not found."  →  exit 0
#
# A ProjectReference whose .csproj was never COPY'd does NOT fail the build. Restore quietly
# skips it, the cached layer is incomplete, and the later `dotnet build` re-restores the gap on
# every single image build — reaching for the network in a step that is supposed to be offline.
# It stays green forever, so nobody notices; it is exactly how Projections, Hateoas and Logging
# fell out of three Dockerfiles at once.
#
# THIS script is the machine that enforces the rule. For each Dockerfile that copies .csproj files
# explicitly, it derives the restore roots from the Dockerfile's own `dotnet restore` commands,
# walks the real ProjectReference graph on disk, and demands that the COPY list cover the full
# transitive closure. The .csproj graph is the single source of truth — the Dockerfile has to
# match it, not the other way round.
#
# Dockerfiles that copy whole source trees (Dockerfile.migrator.prod, Dockerfile.tests) have no
# list to go stale and are skipped.
#
# Run it before pushing; CI (pr-verify + ci) runs it on every PR.
# Keep this in sync with its check-dockerfile-restore-graph.ps1 twin (run locally on Windows).

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"

SEEN=""
MISSING_ON_DISK=""

# Collapse "a/b/../c" to "a/c" without touching the filesystem — the .csproj graph is full of
# ..\ hops and the referenced project may legitimately not exist (that is what we are hunting).
norm_path() {
    printf '%s' "$1" | awk -F/ '{
        n = 0
        for (i = 1; i <= NF; i++) {
            if ($i == "" || $i == ".") continue
            if ($i == "..") { if (n > 0) n--; continue }
            parts[++n] = $i
        }
        out = ""
        for (i = 1; i <= n; i++) out = out (i > 1 ? "/" : "") parts[i]
        print out
    }'
}

project_refs() {
    # $1 = repo-relative .csproj path → repo-relative paths of its direct ProjectReferences.
    # A leaf project (no ProjectReference at all) makes grep exit 1 — expected, not an error.
    local proj="$1" dir ref
    dir="$(dirname "$proj")"
    { grep -oE 'ProjectReference[[:space:]]+Include="[^"]+"' "$REPO_ROOT/$proj" 2>/dev/null || true; } \
        | sed -E 's/.*Include="([^"]+)".*/\1/' \
        | tr '\\' '/' \
        | while IFS= read -r ref; do
              if [ -n "$ref" ]; then norm_path "$dir/$ref"; fi
          done
    return 0
}

walk() {
    local proj="$1" ref
    case "
$SEEN" in *"
$proj
"*) return 0 ;; esac
    SEEN="$SEEN$proj
"
    if [ ! -f "$REPO_ROOT/$proj" ]; then
        MISSING_ON_DISK="$MISSING_ON_DISK$proj
"
        return 0
    fi
    while IFS= read -r ref; do
        if [ -n "$ref" ]; then walk "$ref"; fi
    done <<EOF
$(project_refs "$proj")
EOF
    return 0
}

# Strip comments, then join Dockerfile continuation lines so a `RUN a && \` / `    b` pair reads as
# one command — the order Docker itself uses. Comments must go first or the guard fails OPEN: a
# commented-out `# dotnet restore Foo.csproj` would inject a phantom restore root, satisfying the
# "this Dockerfile restores something" check for a Dockerfile that restores nothing. The COPY
# extraction already ignores comments (it anchors on `^COPY`), so the two must agree.
#
# The continuation join is a no-op on today's Dockerfiles (every restore root shares a line with
# `dotnet restore`), kept because without it a root wrapped onto a bare continuation line drops out
# of the closure and the guard passes vacuously — the very failure mode it exists to catch.
join_continuations() {
    { grep -vE '^[[:space:]]*#' "$1" || true; } | awk '{
        cur = $0
        if (cur ~ /\\$/) { sub(/\\$/, "", cur); buf = buf cur; next }
        print buf cur
        buf = ""
    } END { if (buf != "") print buf }'
}

fail=0
checked=0

while IFS= read -r dockerfile; do
    [ -n "$dockerfile" ] || continue
    rel="${dockerfile#"$REPO_ROOT"/}"

    copied="$(sed -nE 's/^[[:space:]]*COPY \["([^"]+\.csproj)".*/\1/p' "$dockerfile" || true)"
    [ -z "$copied" ] && continue

    roots="$(join_continuations "$dockerfile" \
        | grep -E 'dotnet restore' \
        | grep -oE '[A-Za-z0-9_./-]+\.csproj' \
        | sort -u || true)"

    if [ -z "$roots" ]; then
        fail=1
        printf '✗ %s copies .csproj files but never runs `dotnet restore` on one.\n\n' "$rel"
        continue
    fi

    checked=$((checked + 1))

    SEEN=""
    MISSING_ON_DISK=""
    while IFS= read -r root; do
        if [ -n "$root" ]; then walk "$(norm_path "$root")"; fi
    done <<EOF
$roots
EOF

    if [ -n "$MISSING_ON_DISK" ]; then
        fail=1
        printf '✗ %s restores a project graph that references files which do not exist:\n' "$rel"
        printf '%s' "$MISSING_ON_DISK" | sed 's/^/    /'
        echo
    fi

    needed="$(printf '%s' "$SEEN" | grep -v '^$' | sort -u || true)"
    have="$(printf '%s\n' "$copied" | while IFS= read -r c; do if [ -n "$c" ]; then norm_path "$c"; fi; done | grep -v '^$' | sort -u || true)"
    missing="$(comm -23 <(printf '%s\n' "$needed") <(printf '%s\n' "$have") | grep -v '^$' || true)"

    if [ -n "$missing" ]; then
        fail=1
        printf '✗ %s does not COPY every .csproj its restore graph needs:\n' "$rel"
        printf '%s\n' "$missing" | sed 's/^/    missing: /'
        echo '    (dotnet restore SKIPS these silently — the restore layer is cached incomplete)'
        echo
    fi
done <<EOF
$(find "$REPO_ROOT" -name 'Dockerfile*' -type f \
    -not -path "$REPO_ROOT/.git/*" \
    -not -path "$REPO_ROOT/.claude/*" \
    -not -path '*/node_modules/*' | sort)
EOF

if [ "$fail" -ne 0 ]; then
    echo "──────────────────────────────────────────────────────────────────────"
    echo "Docker restore-graph guard FAILED."
    echo "Add the missing COPY [\"…csproj\", \"…/\"] lines, mirroring the sibling entries."
    echo "See CLAUDE.md → 'Docker restore layers are gated': a new project must join"
    echo "every Dockerfile whose restore graph reaches it."
    exit 1
fi

echo "✓ Docker restore-graph guard passed — $checked Dockerfile(s) copy their full .csproj closure."
