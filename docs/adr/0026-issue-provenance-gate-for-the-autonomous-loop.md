# ADR-0026: Issue provenance gate — only maintainer-written text may drive the autonomous loop

**Status:** Accepted
**Date:** 2026-07-09
**Decision-makers:** Solo maintainer
**Related:** ticket #398 (AUDIT-SEC-08), `docs/claude-loop.md` (the loop manual — Safety model),
`scripts/claude/issue-trust.sh` (the gate), `scripts/claude/next-ticket.sh` (picker),
`scripts/claude/work-ticket.sh` (per-ticket runner), `.claude/commands/work-ticket.md` (the worker
prompt whose contract makes comments authoritative), CLAUDE.md ("Loop mode" — the standing
commit/push/PR/merge authorization this ADR bounds).

## Context

`koniecdev/LotroKoniecDev` is a **public** repository: anyone with a GitHub account can open an
issue on it and comment on any existing one. The autonomous backlog loop turns issues into merged
commits on `main` with no human step in between:

- `next-ticket.sh` picks the next ready issue. Before this ADR it excluded only the labels
  `loop-blocked,qa,post-mvp`, `[Epic]`/`[Tracking]` titles, and issues with an open
  `Depends on #X`. It never looked at **who wrote the issue**, and it required no label at all —
  an unlabeled issue simply sorted last (`else 4`) and was still eligible.
- `work-ticket.sh` feeds `/work-ticket <n>` to a fresh headless Claude whose Bash allowlist
  already contains `git`, `gh`, `dotnet` and `scripts/*`; `LOOP_UNSAFE=1` grants everything.
- Once `pr-verify` is green the same script **auto-squash-merges the PR into `main`** with no
  human review.

`/work-ticket` reads the issue title, body **and comments** as its task, and its own contract says
*"later comments override the body"*. Two facts follow:

- **Attacker-authored text is one hop from `main`.** An outsider's issue is a prompt-injection
  channel into an agent that has write access and merges itself. `gitleaks` blocks committed
  secrets and CodeQL catches some patterns, but a logic backdoor or a data-exfil step is not
  guaranteed to be caught by CI.
- **The author is not the only writer.** Even if every issue were maintainer-authored, anyone can
  append a comment to a maintainer's ticket — and the worker is told to prefer it over the body.

Constraints that shape the fix:

- **GitHub already computes the trust signal.** The REST issue and issue-comment payloads carry
  `author_association` (`OWNER` / `MEMBER` / `COLLABORATOR` = write access; `CONTRIBUTOR`,
  `FIRST_TIME_CONTRIBUTOR`, `MANNEQUIN`, `NONE` = no write access). It is evaluated per read, so
  it tracks permission changes with no state of our own.
  `gh issue list --json` does **not** expose it — only `gh api` does.
- **Labels are already a maintainer-only signal** (applying one needs triage/write access), so
  "carries label X" is a usable second gate — but it is a weaker, indirect statement of the same
  fact the association states directly.
- **The picker is not the trust boundary.** `backlog-loop.sh 123 130` (explicit ticket list) and a
  bare `work-ticket.sh 123` never call `next-ticket.sh`. Gating only the picker would be cosmetic.
- **The `audit` label already claims a human gate it never had.** Its description reads
  *"Finding from an autonomous audit session — triage before /backlog"*, yet it was absent from
  `LOOP_SKIP_LABELS`.

## Decision

### 1. Trust is a property of every writer, checked against `author_association`

An issue is **trusted** when its author *and every one of its comment authors* has an
`author_association` in `LOOP_TRUSTED_ASSOCIATIONS` (default `OWNER,MEMBER,COLLABORATOR`) or a
login in `LOOP_TRUSTED_LOGINS` (default empty — the hook for a second maintainer account or a bot).
Checking comments is not defense-in-depth padding: the worker's own contract makes the last comment
the most authoritative text in the ticket, so an unchecked comment channel would leave the gate
decorative on exactly the tickets an attacker would target.

### 2. The check is one script, `scripts/claude/issue-trust.sh`, and it fails closed

One source of truth for the policy, callable from anywhere: exit `0` trusted, `1` untrusted writer,
`2` error. A missing association, an unknown association, a malformed argument, or **any** GitHub
API failure exits non-zero. A rate-limited or offline `gh` therefore means "refuse", never
"proceed". All diagnostics go to stderr so callers keep their own stdout contract (the picker's
stdout is still exactly one issue number).

### 3. The gate is enforced where untrusted text meets the LLM — `work-ticket.sh` — and mirrored in the picker

`work-ticket.sh` calls the gate **before it spawns the session** and exits `11` on an untrusted
writer, so an explicitly-named ticket cannot bypass it. `next-ticket.sh` calls the same script per
candidate so untrusted work is never even selected (and priority cannot outrank provenance: an
attacker's `critical` ticket loses to a maintainer's `low` one). `backlog-loop.sh` counts an `11` as
*untrusted*, skips it, and does not charge it against the systemic-failure circuit breaker.

An **unreachable API is not a refusal** — it is systemic. `work-ticket.sh` maps a gate error (exit
`2`: unauthenticated, rate-limited, offline) to its own exit `3`, so the conductor's
consecutive-failure breaker stops the run instead of cheerfully "skipping" the whole backlog as
untrusted. Fail-closed must not become fail-silent.

### 4. `audit` joins the default skip labels; explicit naming is the triage step

`LOOP_SKIP_LABELS` defaults to `loop-blocked,qa,post-mvp,audit`, making the `audit` label's stated
intent enforceable. This is a *selection* rule, not a security rule: naming an audit ticket
explicitly (`backlog-loop.sh 391`) still works, and that act of naming **is** the human triage. The
provenance gate keeps firing on explicitly-named tickets — the two rules are deliberately separate.

### 5. The gate is self-tested offline, in CI

`scripts/tests/claude-loop-provenance.tests.sh` stubs `gh` from fixtures (applying the caller's own
`--jq` filter with real `jq`, so the scripts' filters are under test too) and asserts the policy,
the picker's refusal, and that **no `claude` session is spawned** for an untrusted ticket. It runs
in `pr-verify` and `ci` alongside the migration-safety self-test, so the gate cannot rot green.

## Consequences

### Positive

- Attacker-authored issue text can no longer become the task of an agent that pushes and merges.
  The blast radius that led this audit sweep is closed at its narrowest point.
- The rule is one script and one grep-able exit code; every caller (present and future) inherits it.
- Fail-closed means a degraded GitHub API pauses the loop instead of widening it.
- The `audit` label finally means what it says.

### Negative / Accepted Trade-offs

- **A single benign outsider comment (a "+1") strands a ticket** for the loop until a human acts.
  That is the fail-closed behavior we want; the escape hatch is `LOOP_TRUST_GATE=0` (after reading
  the issue *and* its comments yourself), or `LOOP_TRUSTED_LOGINS`. Note the hatch is **run-wide**,
  not per-ticket: set it only on an explicitly-named single ticket, never on a drain.
- **Check-then-use is racy by construction.** The gate reads the comments at T; the worker re-reads
  the issue at T+Δ (Δ = seconds, the session spawn). A comment appended inside that window on an
  already-cleared ticket reaches the worker unchecked. Closing it would require GitHub to hand us
  the text we validated, which the API does not offer. The window is small and the attacker cannot
  observe when it opens; a wider fix (locking the conversation while the loop runs) is not worth it.
- **Two extra API calls per candidate ticket** (issue + comments). The picker returns on the first
  ready ticket, so this is a handful of calls per loop iteration — irrelevant against the rate limit.
- **`author_association` is a permission proxy, not an identity proof.** A compromised collaborator
  account still passes. That risk is out of scope here and is not new.
- The gate protects the *loop*. A human running `/ticket <n>` interactively on a hostile issue is
  still reading attacker text — but a human is in the loop, which is the whole distinction.

## Alternatives Considered

### A. Author + comment association gate at both the picker and the runner (this ADR)

Direct, uses GitHub's own signal, closes the explicit-naming bypass and the comment channel.

### B. Gate only `next-ticket.sh` (as ticket #398 originally proposed)

Rejected: `backlog-loop.sh 123` and `work-ticket.sh 123` skip the picker entirely, so the gate
would not hold on the very path a maintainer uses most. The picker check is kept, but as an
optimization (don't select what would be refused), not as the boundary.

### C. Require a maintainer-only label instead of an author check

Rejected as the primary gate: it works (labels need write access) but states the fact indirectly
and silently couples the security model to labeling hygiene — an unlabeled maintainer ticket would
be refused, and a labeled ticket with a hostile comment would pass. Kept as the *selection* rule
(§4) where that is exactly what is wanted.

### D. Require human approval before every loop merge

Rejected: CLAUDE.md's Loop mode makes commit → push → PR → merge the standing authorization, and
that is the point of an overnight loop. With the input provenance fixed, the agent is acting on
maintainer-written instructions again — which is the trust assumption the merge authorization was
granted under in the first place. If the loop ever runs on tickets from a wider circle, revisit
this line first.

### E. Sanitize/strip untrusted issue text and run anyway

Rejected: there is no reliable sanitizer for natural-language instructions, and a partial one
invites trusting it. Refusing is correct and cheap.

## Implementation Notes

- `gh api repos/{owner}/{repo}/issues/<n>` → `.author_association`;
  `…/issues/<n>/comments --paginate` → `.[].author_association`. `gh issue view --json` has no
  `authorAssociation` field, which is why the gate uses `gh api` rather than the porcelain.
- **`--paginate` is load-bearing**: without it only the first 30 comments are checked and a hostile
  comment posted after 30 benign ones would be invisible. The stub in the self-test models
  pagination, so deleting the flag turns a test red rather than turning the gate off silently.
- A deleted author leaves `user: null`. An unidentifiable writer is refused outright — the
  association alone is never enough. (Splitting the `@tsv` row uses parameter expansion, not
  `IFS=$'\t' read`, which collapses a leading empty field and would shift the association into the
  login.)
- The argument shape-check sits **above** the `LOOP_TRUST_GATE` escape hatch: a guard an env var
  can switch off is not a guard. `work-ticket.sh` validates the issue number independently.
- Both the gate and the picker `cd` to their own checkout before calling `gh`, so
  `repos/{owner}/{repo}` resolves from the repository the scripts live in, not the caller's cwd.
- Allowlists are normalized (whitespace stripped, upper-cased, empty entries dropped) so a trailing
  comma in `LOOP_TRUSTED_ASSOCIATIONS` can never admit an empty association. There is a test for it.
- Exit `11` is new in `work-ticket.sh` and means *untrusted writer*; `backlog-loop.sh` treats it as
  a skip, not a failure. A gate that cannot reach the API surfaces as the ordinary error exit `3`,
  and the picker warns loudly rather than reporting the backlog as drained.
- Every guard above is pinned by a mutation-checked case: removing `--paginate`, the picker gate,
  the worker's refusal, the ghost-account guard, or the comment loop each turns the suite red.

## References

- Ticket #398 — AUDIT-SEC-08 (the finding, from the `security` audit lens, 2026-07-07).
- GitHub REST: `author_association` on issues and issue comments.
- `docs/claude-loop.md` — Safety model, Knobs, Troubleshooting.
