---
name: emit-final-return-after-review-gate
description: After the /security-review or /code-review gate, emit your OWN DONE/BLOCKED wrap-up as the last message — the review output is not your return
metadata:
  type: feedback
---

When the final step before reporting is a review skill (`/security-review`, `/code-review`) or the
`code-reviewer` agent, that tool's output is **not** your return to the orchestrator. End your turn
with your OWN explicit final message: **DONE** (+ PR url, build status, reviewer verdict, file list,
verified-vs-needs-manual-check) or **BLOCKED** (+ the exact open questions).

**Why:** in the #144 run the worker ended three consecutive turns on the `/security-review` text —
no verdict, no PR url, no summary — so the `/backlog` orchestrator had to `SendMessage` three times
to extract the wrap-up, and only then discovered the re-scope work was **staged but never committed
or pushed**. Each round-trip is a wasted spawn cost — the opposite of the cheap, sharp per-ticket
context the loop exists to provide.

**How to apply:** treat the review as a gate, not the finish line. After it returns APPROVE/clean,
do the remaining git steps (commit → PR-body flip to `Closes #<n>` → push) **before** reporting,
then write the explicit DONE/BLOCKED summary as the last thing you say. Never report DONE while
commit/push is still only "staged."
