# ADR-0060: The Loop Stops at the Pull Request, and the Owner Approves by Assigning Themselves

**Status:** Accepted
**Date:** 2026-09-27
**Decision-makers:** Solo maintainer
**Related:** `scripts/claude/backlog-loop.sh`, `scripts/claude/work-ticket.sh`,
`scripts/claude/next-ticket.sh`, `.claude/commands/work-ticket.md`, `.claude/commands/backlog.md`,
`docs/claude-loop.md`, the maintainer's `merge-train` skill (outside this repo); ADR-0026 (its §D is
reversed here); ticket #884 / PR #895 (per-worktree E2E images)

## Context

Until now the backlog loop merged its own work. `work-ticket.sh` waited for `pr-verify`, checked
the CodeQL alerts and ran `gh pr merge --squash`, one ticket after another. ADR-0026 §D rejected a
human approval step on purpose: the loop was built for overnight runs that nobody read.

That is no longer how the owner works. Since the PR and issue texts became readable (September
2026), the owner reads every PR. The flow that grew around it is:

1. A recon session lists independent tickets (for example every open `type-bug` + `area-auth`).
2. Three to five sessions each run `/ticket <n>` in their own worktree cut from `main`.
3. The owner reads each PR, then merges the reviewed batch with the `merge-train` skill.

The loop fit none of this. It merged before anyone read the PR. It ran one ticket at a time. And it
worked in the main checkout: it checked out `main`, pulled, and refused to start on a dirty tree,
so nobody could work there while it ran.

Three more facts shape the decision:

- **GitHub does not let an author approve their own PR.** Every PR here is opened with the owner's
  token, so the "Approve" review is not available. The owner already marks a reviewed PR by
  assigning themselves: #888, #895, #897 and #901 were each assigned on 2026-09-27, hours after
  their last commit.
- **`merge-train` merged every green PR from a trusted author**, reviewed or not, and it passed
  `--delete-branch`, which breaks this repo's rule that branches are never deleted.
- **Parallel worktrees used to break the E2E suites.** Each suite tagged its Docker images with one
  fixed name per machine, so two worktrees could test each other's build (#884). PR #895 tags the
  images per worktree.

## Decision

### 1. The loop never merges

`work-ticket.sh` ends when the worker's PR exists. It does not wait for `pr-verify` and does not
touch the merge button. The conductor ends with a table of the PRs it opened (checks and open
CodeQL alerts as they stand at that moment). That table is the owner's review queue.

### 2. One worktree per ticket, up to three at once

Each ticket runs in `.claude/worktrees/ticket-<n>`, cut from `origin/main` (the name a manual
`/ticket` session uses). The main checkout is never touched. The conductor runs up to three
tickets at once (`-j`, default 3). Three is the global cap on parallel sessions, and it is what the
owner already runs by hand; `-j 1` keeps the old serial behavior. Once a ticket ends, the loop
commits any leftovers to a salvage branch, removes the worktree if it is clean, and removes the
E2E images tagged for it. The branch always stays.

### 3. A ticket already in flight is never started again

A ticket whose branch (`<n>-…`) has an open PR waits for the owner's review, so the picker does not
treat it as ready, and `work-ticket.sh` skips it even when it is named explicitly. An existing
`ticket-<n>` worktree means a session is already on the ticket, so that is skipped too.

### 4. The owner's assignment is the approval

No session and no script ever sets an assignee. The owner assigns themselves after reading a PR.
In this repo, `merge-train` merges a PR written by a person only when:

- the owner is an assignee,
- no commit on the PR that was made outside GitHub is newer than the owner's latest assignment,
- no force push happened after that assignment.

Commits GitHub makes itself (the "update branch" merge commit) do not count: they carry no code the
owner has not seen. So in this repo the train brings a branch up to date with a merge commit, never
a rebase. A rebase is a force push, and it would void the approval it is about to act on. The merge
call passes `--match-head-commit`, so a push that lands between the check and the merge makes the
merge fail instead of merging unread code. To approve again after a push, the owner unassigns and
assigns again.

Bots keep the old rule. Dependabot is trusted by login and needs no assignee: its PRs are version
bumps that CI proves, and the owner never reviewed them one by one.

### 5. The train never deletes a branch here

`merge-train` merges without `--delete-branch` in this repo, as the house rule requires. Other
repos keep their own setting.

### 6. The provenance gate stays

The owner's review is a second line of defence, not a reason to drop the first. `issue-trust.sh`
(ADR-0026) still refuses any ticket with text from someone without write access, in front of every
session.

## Consequences

### Positive

- Nothing reaches `main` before the owner has read it.
- Several tickets run at once, so a batch takes the time of its slowest ticket, not the sum.
- The main checkout stays free while the loop runs.
- There is one merge path, and it is the same for PRs from the loop and from manual sessions.
- An approval does not survive a later push, so the owner never merges code they did not see.

### Negative / Accepted Trade-offs

- Merges wait for the owner. The loop's throughput is now bounded by review time.
- PRs cut from the same `main` can conflict. After the first one merges, the second may need a
  rebase, which is a force push, so it needs a fresh look and a fresh assignment. Picking tickets
  that touch different areas keeps this rare.
- GitHub does not enforce the assignee. Any session holding the owner's token could set it. The
  rule "no session sets an assignee" lives in the worker and `/ticket` prompts, and the staleness
  check limits the damage to code that existed before the owner looked.
- A commit made before the assignment but pushed after it is not caught: the committer date is the
  only per-commit time the API gives. In practice the owner assigns after the push they read.
- PR branches can carry a GitHub merge commit from the train. The squash merge removes it from
  `main`, so `main` stays linear.
- The approval rule lives in the maintainer's `merge-train` script, outside this repo.

## Alternatives Considered

### A. Keep the auto-merge and review after the merge

Rejected. By then the change is on `main` and on staging. A bad change costs a revert PR instead
of a comment, and the review turns into a hunt through history.

### B. A second account opens the PRs, so the owner can use GitHub's "Approve"

Rejected for now. A required review would be enforced by GitHub itself. But it means a second
identity, its token on this machine, and a different git author for every commit, for one
maintainer. The assignee does the same job with no setup. Revisit when a second maintainer joins.

### C. An `approved` label instead of the assignee

Rejected. It is exactly as strong, and the owner already uses the assignee, which every PR list
shows by default.

### D. A required status check that enforces the approval inside GitHub

Rejected for now. A workflow on `assigned` and `synchronize` events could turn the approval into a
check that even a manual `gh pr merge` cannot skip. It costs a workflow run on every assignment and
push, and it would have to tell the train's own branch update apart from a real push. Revisit if a
PR ever merges without approval.

### E. Keep the loop serial

Rejected. With a worktree per ticket the tickets share no files, and the token cost per ticket is
the same either way, so serial only costs wall-clock time. `-j 1` stays available.

## Implementation Notes

- `scripts/claude/work-ticket.sh`: worktree from `origin/main`; skip (exit 12) on an open PR or an
  existing worktree; the session runs inside the worktree; no checks wait, no merge; a clean
  worktree and its E2E images are removed; every exit writes its outcome to the `.meta` file.
- `scripts/claude/backlog-loop.sh`: up to `-j` workers; a usage limit starts nothing new, waits for
  the running tickets, naps and retries the limited ones; a worktree failure (exit 10) or two
  failures in a row stop new starts; TERM stops every worker; the roll-up table lists the PRs.
- `scripts/claude/next-ticket.sh`: a ticket with an open PR is not ready.
- `scripts/tests/claude-loop-conductor.tests.sh` (new, in `pr-verify` and `ci`) pins the conductor;
  `scripts/tests/claude-loop-provenance.tests.sh` gains the in-flight cases.
- `.claude/commands/work-ticket.md`, `ticket.md`: never set an assignee; the wiki path works from a
  worktree. `.claude/commands/backlog.md`, `docs/claude-loop.md`, `CLAUDE.md` (Loop mode) follow.
- The maintainer's `~/.claude-account1/skills/merge-train/merge-train.sh`: the approval check, a
  merge-commit branch update, `--match-head-commit`, and no `--delete-branch` for this repo.

## References

- ADR-0026 — the provenance gate; its §D ("require human approval before every loop merge") is
  reversed by this ADR.
- #884 / PR #895 — per-worktree E2E image tags, the precondition for parallel runs.
- `docs/claude-loop.md` — the loop manual.
