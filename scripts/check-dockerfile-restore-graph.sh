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
# A run that checks no Dockerfile at all is not a pass (#1079). A wrong folder, or a Dockerfile
# renamed out of the `Dockerfile*` pattern, would otherwise switch the guard off while CI stays green.
#
# Exit codes: 0 = every checked COPY list is complete, 1 = a COPY list is incomplete,
# 2 = cannot run, or nothing was checked.
# Run it before pushing; CI (pr-verify + ci) runs it on every PR, right after its self-test
# (scripts/tests/check-dockerfile-restore-graph.tests.sh).
# Keep this in sync with its check-dockerfile-restore-graph.ps1 twin (run locally on Windows).

set -euo pipefail

# In a UTF-8 locale GNU grep hides a matching line that is not valid UTF-8, so a line holding, say, a
# Windows-1250 letter was never checked (#1162). The C locale reads every byte as a plain character.
# There [[:space:]] is ASCII only, but Docker also skips a Unicode space such as U+2003 or U+00A0 at
# the start of a line. So those patterns use [^!-~]: any byte that is not a visible ASCII character.
export LC_ALL=C

# With CDPATH set, `cd` to a relative path may print the folder, and that text would end up here (#1048).
REPO_ROOT="$(CDPATH='' cd -- "$(dirname "$0")/.." && pwd)"

# A COPY or ADD instruction up to its first source: the keyword in any case (Docker lowers it), then
# any flag words such as `--link` or `--chown=app`. Docker splits an instruction on ASCII spaces only,
# and so does [[:space:]] in the C locale. Only the first source is read, because this repo copies one
# project file per line.
COPY_PREFIX='^[^!-~]*([Cc][Oo][Pp][Yy]|[Aa][Dd][Dd])([[:space:]]+--[^[:space:]]*)*[[:space:]]+'
# `dotnet restore` as the shell runs it: any spaces or tabs between the two words, and quotes around
# `restore` if there are any. Or an exec-form RUN ["dotnet", "restore", …] list.
RESTORE_COMMAND="dotnet([[:space:]]+[\"']?|\"[[:space:]]*,[[:space:]]*\")restore"
# When an escape directive at the top of the file sets a backtick, Docker joins the lines that end in
# a backtick instead of a backslash.
ESCAPE_BACKTICK='^[^!-~]*#[^!-~]*escape[[:space:]]*=[[:space:]]*`'

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
    # project_refs cannot tell a project it cannot read from one with no references, and the second
    # is a normal leaf. So check here: a project read as a leaf would hide its whole subtree (#1079).
    if [ ! -r "$REPO_ROOT/$proj" ]; then
        echo "Docker restore-graph guard: could not read $proj." >&2
        exit 2
    fi
    while IFS= read -r ref; do
        if [ -n "$ref" ]; then walk "$ref"; fi
    done <<EOF
$(project_refs "$proj")
EOF
    return 0
}

# Prints the Dockerfile one instruction per line. It follows the rules of Docker's own parser that
# decide where an instruction starts and ends (BuildKit, frontend/dockerfile/parser/parser.go). The
# COPY list and the restore roots are both read from this output, so they cannot disagree on what a
# comment or a continuation is.
#   - Lines end at LF. A CR at the end of a line is dropped, so CRLF line ends read like LF.
#   - A line whose first visible character is `#` is a comment. Comments go first: a commented-out
#     `# dotnet restore Foo.csproj` must not add a restore root to a Dockerfile that restores nothing.
#   - A `\` at the end of a line, with any spaces or tabs after it, joins the next line on. A `\\`
#     there does not, as in Docker.
#   - Inside such a join, an empty line is skipped and the join goes on. A line with no visible ASCII
#     character counts as empty, because the C locale cannot tell a Unicode space from other
#     non-ASCII text. Such a line holds nothing this guard reads. The cost is a false alarm when the
#     line is text and the next line is a COPY, which the join then hides.
# NUL bytes are dropped first, because bash cannot keep one in a variable.
join_continuations() {
    tr -d '\000' < "$1" | awk '{
        line = $0
        sub(/\r+$/, "", line)
        if (line ~ /^[^!-~]*#/) next
        if (joining && line ~ /^[^!-~]*$/) next
        if (line ~ /(^|[^\\])\\[ \t]*$/) { sub(/\\[ \t]*$/, "", line); buf = buf line; joining = 1; next }
        print buf line
        buf = ""
        joining = 0
    } END { if (joining) print buf }'
}

# .git, .claude, node_modules, bin and obj are pruned, so find never enters them. A loop worktree
# added or removed under .claude/, or a folder a build deletes under obj/, cannot make find fail.
# -H: when the repo root itself is a symbolic link, find follows it. Without -H find lists only the link.
if ! dockerfiles="$(find -H "$REPO_ROOT" \
        \( -path "$REPO_ROOT/.git" -o -path "$REPO_ROOT/.claude" -o -name node_modules -o -name bin -o -name obj \) -prune \
        -o -name 'Dockerfile*' -type f -print | sort)"; then
    echo "Docker restore-graph guard: could not list the Dockerfiles under $REPO_ROOT." >&2
    exit 2
fi

fail=0
checked=0

while IFS= read -r dockerfile; do
    [ -n "$dockerfile" ] || continue
    rel="${dockerfile#"$REPO_ROOT"/}"

    if ! instructions="$(join_continuations "$dockerfile")"; then
        echo "Docker restore-graph guard: could not read $rel." >&2
        exit 2
    fi

    # Docker takes the directive only at the top of the file. The guard refuses it on any comment
    # line: a false alarm costs less than a restore root that the join above would hide.
    if grep -aqiE "$ESCAPE_BACKTICK" "$dockerfile"; then
        echo "Docker restore-graph guard: $rel sets a backtick as its escape character, and this guard joins lines only on a backslash." >&2
        exit 2
    fi

    copied="$(printf '%s\n' "$instructions" | sed -nE \
        -e "s/${COPY_PREFIX}\[[[:space:]]*\"([^\"]+\.csproj)\".*/\3/p" \
        -e "s/${COPY_PREFIX}[\"']?([^[:space:][\"'][^[:space:]\"']*\.csproj)[\"']?[[:space:]].*/\3/p")"
    [ -z "$copied" ] && continue

    roots="$(printf '%s\n' "$instructions" \
        | grep -E "$RESTORE_COMMAND" \
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
$dockerfiles
EOF

if [ "$fail" -ne 0 ]; then
    echo "──────────────────────────────────────────────────────────────────────"
    echo "Docker restore-graph guard FAILED."
    echo "Add the missing COPY [\"…csproj\", \"…/\"] lines, mirroring the sibling entries."
    echo "See CLAUDE.md → 'Docker restore layers are gated': a new project must join"
    echo "every Dockerfile whose restore graph reaches it."
    exit 1
fi

if [ "$checked" -eq 0 ]; then
    echo "Docker restore-graph guard: no Dockerfile under $REPO_ROOT copies a .csproj list, so nothing was checked." >&2
    echo "Check the path, and that the image Dockerfiles still match 'Dockerfile*'." >&2
    exit 2
fi

echo "✓ Docker restore-graph guard passed — $checked Dockerfile(s) copy their full .csproj closure."
