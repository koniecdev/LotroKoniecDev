---
description: Rewrite a PR body, an issue comment or a Markdown file into plain B2 English that a non-native reader understands on the first read — every fact kept, nothing added
argument-hint: <PR number | issue comment URL | file path>
---

Rewrite **$ARGUMENTS** into plain **B2 English**. The reader is a developer or a tester whose first
language is not English. They must understand every line on the first read, without a dictionary.

Why this is its own step: "write in simple English" rules in `CLAUDE.md` do not work for PR bodies.
A session that just wrote the code writes for itself — dense, clever, full of its own shorthand.
TheKittySaver PR koniecdev/TheKittySaver#815 is the precedent; the command is ported from
koniecdev/TheKittySaver#816. It is the same idea as the B1-B2 rule for code comments (#675), applied
to what people read on GitHub. A separate pass with one job fixes that.

## 1. Get the text

- A PR number → `gh pr view <n> --json body -q .body`
- An issue comment URL (`…/issues/<n>#issuecomment-<id>`) → `gh api repos/{owner}/{repo}/issues/comments/<id> -q .body`
- A file path → read the file.

Save the original to the scratchpad as `b2-original.md` before you change anything.

## 2. Rewrite — the rules

**Keep every fact. Add no fact.** You change the language, never the content. When you do not
understand a sentence, keep its meaning and simplify only the grammar — do not guess.

Keep **exactly as they are**:
- `Closes #<n>` — the first line, untouched.
- The heading `## Ticket report` and the five bold labels inside it (`**Shipped:**`, `**Proof:**`,
  `**Assumptions:**`, `**Doubts:**`, `**Follow-ups:**`). Later sessions look for these exact names.
- Every ticket/PR/ADR/spec number, file path, `code identifier`, URL, number, test count, version
  and CodeQL alert id.
- Every Polish string in „…" (translations, UI copy) — character for character. Do not translate it.
- The attribution footer at the end.

Language:
- **One idea per sentence.** Aim for 15 words, hard stop at about 25. Split long sentences.
- **Active voice, simple tenses.** "We added X", "The patcher now writes Y". Not "X has been made to…".
- **Common words.** Write "hide", not "leave the public surface". "Delete", not "purge". "Check",
  not "assert". "Because", not "since"/"as". When a technical term is needed, keep it and explain it
  once in a few plain words.
- **No idioms, no metaphors, no clever phrasing:** "load-bearing", "by construction", "fail-open",
  "in flight", "smells", "for free", "stamped", "dressed up as", "leaves nothing behind".
- **No long dashes (—) joining ideas, no semicolons, no nested brackets.** Use a new sentence or a
  bullet instead.
- **Spell out** shorthand the reader cannot guess: "pl + en" → "Polish and English", "⇒" → "so",
  "≤ 2 min" → "up to 2 minutes".
- Keep `code` in backticks, but never make a sentence depend on reading the identifier. First say
  what it does in words, then give the name.

Structure:
- A paragraph longer than 3 sentences becomes bullets. One fact per bullet.
- Keep the original section order. You may split a section into short sub-parts with bold lead-ins
  (`**Technical details:**`).
- Manual check steps: numbered, one action per step, and the expected result on its own line after
  **Expected:**.
- Inside `## Ticket report`, turn a long label value into sub-bullets under the label.
- No intro and no outro. Never write "Here is a rewritten version…". The output is the text itself.

Tone: direct and neutral. No praise of the work ("perfectly", "robust", "elegant").

## 3. Check that no fact got lost

Write the rewrite to the scratchpad as `b2-rewrite.md`, then run this from the scratchpad folder
(literal file names, no variables):

```bash
python3 - b2-original.md b2-rewrite.md <<'PY'
import re, sys
old, new = (open(p, encoding="utf-8").read() for p in sys.argv[1:3])
patterns = [r"#\d+", r"`[^`\n]+`", r"https?://[^\s`)>\]]*[^\s`)>\].,;:]", r"„[^”\"\n]+[”\"]", r"\b\d[\d.,/]*\b", r"ADR-\d+"]
missing = sorted({m for p in patterns for m in re.findall(p, old) if m not in new})
print("\n".join(missing) if missing else "OK: every number, identifier, URL and UI string is still there")
PY
```

Every line it prints is a fact that disappeared. Put each one back — or, only when the rewrite says
the same thing in another correct form (for example a number moved into a code span), leave it and
name it in your reply. Then read both texts side by side one last time: same claims, same caveats,
same order.

## 4. Apply

- A PR → `gh pr edit <n> --body-file <absolute path>/b2-rewrite.md`. Editing the body does not
  re-run `pr-verify` or any other workflow (none listens to the `edited` event), so this is free.
- An issue comment → `gh api -X PATCH repos/{owner}/{repo}/issues/comments/<id> -F body=@<absolute path>/b2-rewrite.md`
- A file → overwrite it.

Run every `gh` command from the repo root and give the scratchpad file by its absolute path. `gh`
finds the repo (and fills `{owner}/{repo}`) only inside a git checkout.

Reply with one line: what was rewritten, and the list from step 3 if anything was left on purpose.
