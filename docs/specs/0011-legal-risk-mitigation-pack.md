# Spec 0011: Legal-risk mitigation pack (IP/ToS exposure towards SSG + player-facing disclaimers)

- **Status:** Agreed
- **Date:** 2026-07-12
- **Author:** Artur Koniec (research + drafting: Claude)
- **Ticket:** #474–#479 (LEGAL-08…LEGAL-13) under epic #459
- **Related:** spec 0010 (terms of service), LEGAL-03 (ToS page), LEGAL-05 (privacy/footer links),
  `docs/RUSSIAN_PROJECT_RESEARCH.md`, `docs/knowledge-base/russian-project.md`

> **Not legal advice.** This spec is an engineering-level risk-reduction plan grounded in
> precedent research and a repo audit. For formal comfort (esp. before any monetization or a
> formal entity), a Polish IP lawyer should review the final texts. The goal is to make the
> project a *hard, unattractive target* and to make the worst realistic case (a takedown
> request) cheap and fast to comply with — not to manufacture immunity, which does not exist.

## Business context

The project modifies LOTRO's client data files and republishes translated (derivative) game text.
SSG's LOTRO Code of Conduct prohibits modifying the game client and distributing third-party
tools without express written permission — so the project operates, like every fan localization
before it (RU, ES), on *tolerated* rather than *licensed* ground. The GDPR pack (#459) covered
the users' side; this pack covers the owner's side: minimize what a rightsholder could object to,
make compliance-on-request instant, and tell players honestly-but-calmly where they stand.

## Risk assessment (research findings, 2026-07-12)

**What the rules say.** The LOTRO Code of Conduct (help.standingstonegames.com) prohibits
modifying the game client and creating/distributing third-party tools without express written
permission; violation risk for *players* is account suspension/termination. The official
trademark line (lotro.com footer, 2026): SSG marks are trademarks of **Daybreak Game Company
LLC**; *The Lord of the Rings Online* and the characters/items/events/places therein are
trademarks of **Middle-earth Enterprises, LLC** used under license.

**What actually happens.** The tolerance record is long and consistent:

- The Russian project (translate.lotros.ru + Elanor/"Nasledie") has operated **publicly since
  the early 2010s** — public web platform, public GitHub repos (Endevir/Elanor,
  LOTRO-Enchanced-text-patcher, since ~2016–2017) that **redistribute `datexport.dll`** — with
  no documented takedown, lawsuit, or ban wave. (The claimed LOTRO Beacon mention of the RU
  project is user-reported; not verified in search — Beacon does routinely spotlight fan
  projects.)
- The Spanish project (lotroesp.es + LOTRO ESP Discord) distributes a translation patch openly,
  with Steam guides and YouTube tutorials, unimpeded.
- LOTRO Companion and its `lotro-data` GitHub repo (extracted game data) have existed for years
  untouched; DATUnpacker mirrors hosting `datexport.dll` date to 2011.
- **No documented ban for using a translation patch was found.**

**Realistic worst-case ladder:** (1) a C&D / DMCA notice to GitHub or the domain → comply fast,
project continues in reduced form; (2) player account action → theoretical, unprecedented for
translations; (3) an actual lawsuit → economically irrational against a non-commercial fan
project in the EU with a compliance posture, and unprecedented in this fandom. The mitigation
plan therefore optimizes for step (1): reduce objectionable surface, respond instantly.

**Where the exposure actually sits (repo/platform audit):**

| # | Exposure | Where | Severity |
|---|---|---|---|
| E1 | `datexport.dll` (Turbine proprietary) + MSVC runtimes committed to the **public** repo | `src/Patcher/LotroKoniecDev.Infrastructure/*.dll` | highest-value takedown target; precedent says tolerated |
| E2 | Verbatim **English** game text (one full quest dialog) + real PL quest translation | `translations/polish.txt`, `docs/knowledge-base/update-48.0/polish-pre-48.txt`, `…48.7/polish-pre-48.7.txt` | small volume, but literal copyrighted text in a public repo |
| E3 | Full EN corpus **publicly browsable without login** | `ListTranslations` + `/translations` page are `AllowAnonymous` | "making available" of the full text corpus — the largest content exposure by volume |
| E4 | README says "Open-source" but repo has **no LICENSE file**; no third-party notices | `README.md` §"License & disclaimer" | inconsistency; unclear code/content boundaries |
| E5 | No non-affiliation/trademark line in the web footer (only inside ToS §1.2) | `MainLayout.razor` footer | standard-practice gap, trivial fix |
| E6 | No ToS clause on rightsholder IP / takedown compliance | `Terms.razor` | posture gap — the "cooperative stance" is undocumented |
| E7 | CLI prints no disclaimer; download CTA has no risk note | `Cli`, `Home.razor` download section | player-facing honesty gap |

> **E2 audit update (LEGAL-08, 2026-07-12):** the repo-wide grep found more than the table row —
> `translations/example_polish.txt` carried the full verbatim EN quest dialog, and two tracked
> full-export diffs (`update-48.0/diff-47.2-vs-48.0.txt`, 694 KB; `update-48.7/diff-48.0-vs-48.7.txt`,
> 131 KB) contained thousands of lines of verbatim EN game text. Quest lines were replaced with
> synthetic equivalents; the diff files were removed from the repo (they survive in the gitignored
> `intel/`, and their stats live in the RESULTS.md files). Short functional UI labels and location
> names were kept as de minimis.

**What is already fine:** ToS §1.2 non-affiliation + IP acknowledgment, §1.3 non-commercial,
§5 contributor license, §7 as-is/own-risk/consumer-rights (LEGAL-03); `data/` (full EN export)
is untracked; test fixtures are synthetic; the distribution artifact (`polish.txt` via
`GET /translation-files/{lang}`) contains **only Polish text + numeric IDs, never the English
source**; zero game artwork in the repo; fonts self-hosted (LEGAL-06).

## Goal

After this pack, the owner's realistic worst case is "receive a request → comply within days,
project survives", players see a calm, honest one-liner about game-rules risk, and nothing in
the public repo or platform hands a rightsholder an easy, high-value grievance.

## In scope

- **LEGAL-08 — Repo content hygiene.** Replace the two verbatim quest lines (EN original + real
  PL translation) in `translations/polish.txt` and both `docs/knowledge-base/update-48.*` files
  with synthetic long-form equivalents preserving format/length/args characteristics (the KB
  value is the empirical result, not the literal text — keep each file's header note and add
  one line: "content redacted to synthetic equivalents 2026-07; empirical result unaffected").
  Repo-wide grep for any other verbatim game text (docs, ADRs, tests) and replace likewise.
- **LEGAL-09 — LICENSE + THIRD-PARTY-NOTICES.** Add the **MIT** LICENSE for **our code** (Q3),
  plus `THIRD-PARTY-NOTICES.md` stating: `datexport.dll` is a proprietary Turbine/SSG
  library circulating in fan tooling since ~2011, included solely for interoperability, **not**
  covered by the repo license, removed promptly on rightsholder request; MSVC runtime DLLs are
  Microsoft redistributables. README "License & disclaimer" section updated to match (fix the
  "Open-source with no license" contradiction), mirroring the official 2026 trademark line
  (Daybreak Game Company LLC for SSG marks; Middle-earth Enterprises, LLC for Tolkien marks).
- **LEGAL-10 — Footer non-affiliation line** (`MainLayout.razor`): one sentence under the
  existing footer links, e.g. *"Nieoficjalny, niekomercyjny projekt fanowski — niepowiązany ze
  Standing Stone Games ani Middle-earth Enterprises. The Lord of the Rings Online™ oraz nazwy
  postaci, przedmiotów, wydarzeń i miejsc są znakami towarowymi Middle-earth Enterprises, LLC."*
- **LEGAL-11 — ToS IP/takedown clause** (new § in `Terms.razor`, renumber TOC): the Operator
  respects the rights of SSG/MEE; English source texts are processed solely to enable the
  creation of the Polish translation and are not part of the published file; the published file
  contains exclusively community-created Polish text; justified rightsholder requests
  (kontakt: koniecdev@gmail.com) are honored promptly, including removal of content or
  suspension of distribution.
- **LEGAL-12 — Player-facing risk note, non-scary.** (a) Home download section (`Home.razor`):
  one sentence + ToS link, e.g. *"Spolszczenie modyfikuje pliki gry — formalnie regulamin LOTRO
  nie przewiduje takich modyfikacji, choć przez ponad dekadę działania analogicznych projektów
  (rosyjskiego i hiszpańskiego) nie odnotowano za nie banów. Korzystasz na własną
  odpowiedzialność."* (b) CLI: same message as a one-line dimmed notice printed by `patch` and
  `launch` (Spectre.Console, no confirmation prompt — do not annoy), plus a `NOTICE` file in
  release artifacts. (c) A note in the M4 epic that the Avalonia app ships the same text in its
  About/first-run.
- **LEGAL-13 — Takedown playbook** (`docs/legal/takedown-playbook.md`, internal): on C&D/DMCA —
  acknowledge within 48 h, comply first argue never (disable `GET /translation-files/{lang}` /
  remove the named asset / take the repo private as applicable), keep records, template reply
  in EN. Include the GitHub DMCA process link and the domain registrar contact path.
- Per Q5's answer: possibly naming the owner as Operator in ToS §1.1 and as data controller in
  the privacy policy §01 (folds into LEGAL-11).

## Out of scope

- Forming a legal entity (stowarzyszenie/fundacja) — conscious accept: Operator remains a
  natural person behind the "społeczność lotro-translator.pl" label (revisit if the project
  monetizes or takes donations — it currently does neither, ToS §1.3).
- Renaming the project/domain away from "lotro" — nominative fair use posture + disclaimers
  instead; a rename would be disproportionate (RU/ES precedents use the mark identically).
- Any monetization/donations gating — staying strictly non-commercial *is* the mitigation.
- Rewriting git history (E1/E2 purge from old commits) — decide in Q1/Q4; default is
  HEAD-only cleanup.
- The M4 app itself (only the wording note lands in the epic).

## Business rules & edge cases

- Player-facing wording must be **honest but calm**: state the formal rule-break plainly, state
  the empirical record plainly, never promise safety, never use scare-words ("ban", "pozew") in
  headline copy. One sentence, always adjacent to the download/patch action, always linking ToS.
- ToS changes ship under the existing §8 change process (14-day e-mail notice to registered
  users) — but pure *additions* protective of users and third parties may ship immediately with
  the dated-changelog note, consistent with §8.2.
- The takedown clause must not promise more than we can do (no "within 24 h" SLA in public
  text; "niezwłocznie/promptly" only). The 48 h target lives in the internal playbook.
- `THIRD-PARTY-NOTICES.md` must not claim any license we don't have for `datexport.dll` —
  provenance + interoperability purpose + removal-on-request, nothing more.
- Synthetic replacement lines in E2 files must keep: `||` field structure, `\n`/`\q` escapes,
  `<--DO_NOT_TOUCH!-->` placeholders, args ordering, realistic lengths — golden-fixture-adjacent
  files still must exercise the same parser paths. All existing tests stay green untouched
  (patcher-stable rule).
- The distribution artifact keeps its "PL-text-only, never EN source" property permanently —
  state it in the ToS clause (LEGAL-11) and treat any future change to that property as
  ADR-worthy.

## Contract

- No new endpoints, commands, or schema. Touched surfaces:
  - `src/Frontend/…/Layout/MainLayout.razor` (footer line),
  - `src/Frontend/…/Pages/Terms/Terms.razor` (new §, TOC renumber),
  - `src/Frontend/…/Pages/Home/Home.razor` (download note),
  - `src/Patcher/LotroKoniecDev.Cli` (one-line notice in `patch`/`launch` output — additive,
    no behavior/exit-code change),
  - `LICENSE`, `THIRD-PARTY-NOTICES.md`, `README.md`, `docs/legal/takedown-playbook.md`,
  - `translations/polish.txt`, `docs/knowledge-base/update-48.0/polish-pre-48.txt`,
    `docs/knowledge-base/update-48.7/polish-pre-48.7.txt`,
  - per Q2: possibly `ListTranslations.cs` / `Translations.razor` auth attributes.

## Acceptance criteria

- [ ] No verbatim LOTRO text (EN or translated-from-real-quest PL) remains anywhere in the
      repo HEAD (`grep` audit documented in the PR).
- [ ] `LICENSE` + `THIRD-PARTY-NOTICES.md` exist; README section consistent with them and with
      the official 2026 trademark ownership line.
- [ ] Every frontend page renders the footer non-affiliation/trademark line (SSR-pure, no new
      interactivity; `check-ssr-purity` green).
- [ ] ToS shows the new IP/takedown §, TOC updated, changelog date bumped.
- [ ] Home download CTA and CLI `patch`/`launch` output carry the agreed one-liner; CLI exit
      codes and all existing patcher tests unchanged.
- [ ] `docs/legal/takedown-playbook.md` exists with the 48 h acknowledge flow + reply template.
- [ ] Q2's conscious accept (public EN corpus stays anonymous) and Q4's no-outreach stance are
      recorded here and require no code change; nothing in this pack touches endpoint auth.
- [ ] Zero warnings; existing tests green with assertions untouched.

## Open questions

**Empirical — answered:**

- *Does the published `polish.txt` contain English source text?* No — content field carries the
  Polish text only (format digest in `CLAUDE.md`; `GetTranslationFile` serves the pre-built
  PL artifact).
- *Is the EN corpus public?* Yes — `ListTranslations.cs:193` `.AllowAnonymous()` +
  `Translations.razor` `[AllowAnonymous]`.
- *Do peer projects redistribute `datexport.dll` publicly?* Yes — Endevir repos and DATUnpacker
  mirrors, for 9–15 years, no takedowns found.
- *Documented bans for translation patches?* None found (searched 2026-07-12).

**Business — answered by the owner (2026-07-12):**

- **Q1 — `datexport.dll` in the public repo:** **keep** (option a) — THIRD-PARTY-NOTICES +
  instant-removal-on-request commitment; precedent-backed (Endevir/DATUnpacker, 9–15 years).
- **Q2 — public EN corpus browsing:** **keep anonymous for now — no change in this pack.**
  Conscious accept of exposure E3; owner will revisit (likely login-gate) at his own call
  later. Nothing to implement now.
- **Q3 — code license:** **MIT** for our code; game-content and `datexport.dll` carve-outs in
  LICENSE preamble note + THIRD-PARTY-NOTICES.
- **Q4 — proactive SSG outreach:** **no — silence for now.** Not asking preserves the good-faith
  posture; may be revisited (the outreach-letter ticket is NOT cut).
- **Q5 — ToS/privacy-policy Operator identity.** Current state ("społeczność
  lotro-translator.pl" as Operator in ToS §1.1 *and* as data controller in privacy policy §01)
  is a formal defect: a "społeczność" has no legal personality, UŚUDE art. 5 requires the
  service provider to be identified (name + contact; non-compliance is technically a fineable
  wykroczenie, art. 23), RODO art. 13(1)(a) requires the controller's *identity* (factually the
  owner), and the §5 contributor license — the clause protecting the owner from his own
  translators — is granted to a non-entity, which weakens it. Since the owner's full name is
  already public (README © line, git commits), naming him costs ~zero extra exposure.
  Recommendation: name the owner in ToS §1.1 + privacy policy §01 (name + e-mail, no home
  address — pragmatic hobby-project middle ground; residual UŚUDE address gap consciously
  accepted). **Decision (2026-07-12): name the owner — "Artur Koniec, prowadzący niekomercyjny
  projekt lotro-translator.pl" in ToS §1.1 and privacy policy §01; folds into LEGAL-11.**

## Assumptions

- The project stays non-commercial (no ads, donations, or paid tiers) — several mitigations
  lean on this.
- Staging/prod deploy of frontend/ToS changes rides the normal CD; no migration involved.
- The `||` contract and golden fixtures are untouched — synthetic replacements only in the two
  KB files and `translations/polish.txt`, which are not parser golden fixtures.
