# ADR-0060: The Loop Stops at the Pull Request, and the Owner Approves by Assigning Themselves

**Status:** Accepted
**Date:** 2026-09-27
**Decision-makers:** Solo maintainer
**Related:** `scripts/claude/backlog-loop.sh`, `scripts/claude/work-ticket.sh`,
`scripts/claude/next-ticket.sh`, `.claude/commands/work-ticket.md`, `.claude/commands/backlog.md`,
`docs/claude-loop.md`, the maintainer's `merge-train` skill (outside this repo); ADR-0026 (its §D is
reversed here); ticket #884 / PR #895 (per-worktree E2E images)

## Amendment (2026-09-29): a worktree kept after a usage limit is resumed, not skipped

**Narrows §3.** When a worker hits the usage limit, it now keeps the ticket's worktree exactly as
the session left it and records the session in a marker inside that worktree's git folder (#934).
The next run of the ticket resumes that session there instead of skipping the ticket as "already in
flight", and the picker no longer hides such a ticket. Any other worktree still means a session is
on the ticket, and an open PR still means the ticket waits for review, with one exception: an open
PR from the kept worktree's own branch belongs to the session that is resumed. Nothing here merges
or assigns. Details: `docs/claude-loop.md`, "A session stopped by a usage limit, or DONE without a
PR".

## Amendment (2026-09-28): rebase only, and the push log decides what the owner read

**Supersedes §4's "a merge commit, never a rebase" and its push-time rules.** The owner's rule: a
feature branch is brought up to date by a rebase only, whatever tool does it. A merge of `main`
into a branch never happens.

The merge-commit update had a cost nobody saw on 2026-09-27. Once a branch holds a merge of `main`,
GitHub's rebase button can no longer update it: a rebase replays each commit on its own and meets
again the conflict that the merge had already resolved. PR #904 got stuck exactly like that.
Sessions copied the idea too, and "fixed" a conflict with a local `git merge origin/main`.

A rebase is a force push, and until now any force push after the assignment voided the approval.
The review of this change also found two older holes in the gate:

- The push time of a commit was its first check suite. That is the first time GitHub saw the
  commit anywhere, not the time it reached this PR. A commit dropped before the review and pushed
  back after it passed as "old".
- Any two-parent commit made by GitHub counted as "brings in `main` and nothing else". A conflict
  resolved in GitHub's web editor makes exactly such a commit, and it holds new code.

So the gate now works from GitHub's push log for the branch (the Activity API). It lists every
push and force push, with the commit before and after it:

- The approved commit is the head after the last push before the owner's latest assignment. A
  push in the same second as the assignment counts as after it, so a tie refuses.
- If nothing was pushed after the assignment, the PR head must be the approved commit.
- If something was, the train fetches the approved commit, the head and `main` into a cache repo.
  Then it merges the approved commit into the `main` commit that the head sits on
  (`git merge-tree`). The approval holds only when that merge is clean and its tree is exactly the
  head's tree. A conflict resolution, extra code, or a rebase onto another branch is code the owner
  has not read. The owner reads it and assigns again.
- A branch that holds a merge commit is refused, in every repo, whatever made it.
- The branch must live in this repo. The push log of a fork's branch cannot be read, so such a PR
  is refused.

The train updates a branch that is behind with `gh pr update-branch --rebase`. When GitHub cannot
rebase it (a conflict), the train leaves the PR for a person and never falls back to a merge
commit. The fix is a local `git rebase origin/main` and a `--force-with-lease` push. The same check
applies to that push, so a clean local rebase keeps the approval too.

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
- **Parallel worktrees break the E2E suites until PR #895 lands.** Each suite tags its Docker
  images with one fixed name per machine, so two worktrees can test each other's build (#884).
  PR #895 tags the images per worktree.

## Decision

### 1. The loop never merges

`work-ticket.sh` ends when the worker's PR exists (an open PR in this repo whose branch belongs
to the ticket). It does not wait for `pr-verify` and does not touch the merge button. The
conductor ends with a table of the PRs it opened (checks and open CodeQL alerts as they stand at
that moment). That table is the owner's review queue.

Old loop code merged PRs, and the loop scripts run from whichever checkout starts them. So from
this change on, the conductor refuses to start when `scripts/claude/` there differs from
`origin/main` (`LOOP_ALLOW_LOCAL_SCRIPTS=1` is the explicit way round it, for work on the loop
itself). A branch cut before this change has no such guard and still runs the old conductor, so
the checkout that starts the loop must be brought up to date with `main` once.

### 2. One worktree per ticket, up to three at once

Each ticket runs in `.claude/worktrees/ticket-<n>`, cut from `origin/main` (the name a manual
`/ticket` session uses). The main checkout is never touched. The conductor runs up to three
tickets at once (`-j`, default 3). Three is the global cap on parallel sessions, and it is what the
owner already runs by hand; `-j 1` keeps the old serial behavior, and it is the setting to use
until PR #895 lands. Once a ticket ends, the loop commits any leftovers to a salvage branch (and
gives one to commits made on no branch, since removing a worktree drops its reflog), removes the
worktree, and removes the E2E images tagged for it. A rebase or merge left half done is never
committed over: that worktree stays as it is, for the owner. The branch always stays. Stopping the
loop stops each session and every process group it started (Claude Code runs each Bash command
in a group of its own), then salvages and cleans up the same way.

### 3. A ticket already in flight is never started again

A ticket whose branch (`<n>-…`) has an open PR waits for the owner's review, so the picker does not
treat it as ready, and `work-ticket.sh` skips it even when it is named explicitly. An existing
`ticket-<n>` worktree means a session is already on the ticket, so that is skipped too. (Narrowed
2026-09-29, see the amendment above: a worktree the loop kept after a usage limit is resumed.)

### 4. The owner's assignment is the approval

No session and no script ever sets an assignee. The owner assigns themselves after reading a PR.
In this repo, `merge-train` merges a PR written by a person only when:

- the owner is an assignee, and the owner is the one who assigned them;
- the branch's push log shows which head the owner read: the one left by the last push before the
  owner's latest assignment;
- nothing was pushed after that, or everything pushed after it adds up to that head rebased onto
  `main` and nothing more;
- the branch holds no merge commit, and it lives in this repo, not in a fork.

(Amended 2026-09-28, see the amendment above. The first version took each commit's push time from
its first check suite and let GitHub's own two-parent merge through.) The train brings a branch up
to date with a rebase, never a merge commit. It checks the approval before that update and again
right before the merge, and the merge call passes `--match-head-commit`, so a push that lands after
the last check makes the merge fail instead of merging unread code. To approve again after a push,
the owner unassigns and assigns again.

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
  rebase. When that rebase needs a conflict resolution, the resolution is new code, so it needs a
  fresh look and a fresh assignment. Picking tickets that touch different areas keeps this rare.
- GitHub does not enforce the assignee. Any session holding the owner's token could set it. The
  rule "no session sets an assignee" lives in the worker and `/ticket` prompts, and the staleness
  check limits the damage to code that was on GitHub when the owner assigned themselves.
- The gate knows when the owner clicked, not what the owner saw. A push that lands while the owner
  reads, before the click, counts as read. So the owner reloads the PR right before assigning.
- The gate trusts GitHub's push log (the Activity API). A branch whose log is missing, or does not
  reach the PR head yet, is refused until it does (amended 2026-09-28).
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
  existing worktree; the session runs inside the worktree in its own process group, and a stop,
  a timeout or a SIGKILL of the worker ends its whole process tree; no checks
  wait, no merge; the PR it reports must be open and belong to the ticket; salvage, then the
  worktree and its E2E images are removed; every exit writes its outcome to the `.meta` file.
- `scripts/claude/backlog-loop.sh`: up to `-j` workers; refuses loop scripts that differ from
  `origin/main`; a usage limit starts nothing new, waits for the running tickets, naps and retries
  the limited ones; a worktree failure (exit 10) or two failures in a row stop new starts; TERM
  stops every worker at once, even mid-nap; the roll-up table lists the PRs.
- `scripts/claude/next-ticket.sh`: a ticket with an open PR or a worktree is not ready (except a
  worktree kept after a usage limit — see the 2026-09-29 amendment).
- `scripts/tests/claude-loop-conductor.tests.sh` (new, in `pr-verify` and `ci`) pins the conductor;
  `scripts/tests/claude-loop-provenance.tests.sh` gains the in-flight cases.
- `.claude/commands/work-ticket.md`, `ticket.md`: never set an assignee; the wiki path works from a
  worktree. `.claude/commands/backlog.md`, `docs/claude-loop.md`, `CLAUDE.md` (Loop mode) follow.
- The maintainer's `~/.claude-account1/skills/merge-train/merge-train.sh`: the approval check
  (the push log and the tree check since 2026-09-28), a rebase-only branch update, the merge-commit
  refusal, `--match-head-commit`, and no `--delete-branch` for this repo, with its own offline
  self-test `merge-train.tests.sh` next to it.

## References

- ADR-0026 — the provenance gate; its §D ("require human approval before every loop merge") is
  reversed by this ADR.
- #884 / PR #895 — per-worktree E2E image tags, the precondition for parallel runs.
- `docs/claude-loop.md` — the loop manual.
