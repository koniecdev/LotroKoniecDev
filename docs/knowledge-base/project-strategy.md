# Project Strategy: OSS + Controlled Translations

> **Status note (2026-07-11):** point-in-time strategy snapshot; the direction stands. Two details
> have since been superseded: "MediatR handlers" → in-house handler interfaces (ADR-0001, no
> mediator), and "two-source crowdsource+vnum update detection" → forum-only detection (spec 0001;
> see [update-detection-strategy.md](update-detection-strategy.md)). The web platform (M3) has
> shipped; placeholder validation landed in the editor (`PlaceholderAnalyzer`).

## Decision: Open Source Code, Controlled Translations

### Code (patcher + web app): OSS on GitHub
- Builds trust (players see no malware)
- Attracts developers
- MIT or GPL license

### Translations: Controlled via web platform
- Translators register, submit via web UI
- Moderator/admin reviews and approves
- Only approved translations go into patch files
- Single canonical version (no competing forks)

### Why forks won't be a problem
- Too small a niche (Polish LOTRO players)
- Official version has momentum
- Maintaining translations after each game update is real work
- Community prefers contributing to one project

## Web Platform Model (proven by Russians)

Translators submit through web UI → no git/PR knowledge needed.
Review workflow ensures quality. Style guide + glossary ensure consistency.

### Critical Missing Pieces (add to plan)

1. **Glossary table** - `GlossaryTerms(EnglishTerm, PolishTerm, Notes, Category)`
   - Tolkien proper nouns (Shire = Shire vs Hrabstwo?)
   - Game-specific terms (Fellowship, Deed, Trait, etc.)
   - Translator sees suggestions while translating

2. **Style guide page** in web app
   - per/ty in dialogs
   - Polish diacritics handling
   - Tolkien name conventions (follow Polish book translations?)
   - Placeholder rules

3. **Placeholder validation on save** (planned as #36)
   - Count `<--DO_NOT_TOUCH!-->` in source vs translation
   - Warn if mismatch
   - Russians fall back to English on error; we use Result monad

## Comparison: Us vs Russians

| Aspect | Russians | Us | Winner |
|---|---|---|---|
| Architecture | Monolith WPF | Clean Architecture 5 layers | Us |
| Error handling | try/catch | Result monad | Us |
| Tests | None visible | ~550 assertions | Us |
| Web platform | Working, years of data | Planned (M3) | Them (for now) |
| DAT protection | -disablePatch flag | None needed (proven unnecessary) | Us |
| Update detection | NinjaMark (reactive) | Two-source: crowdsource vnum + forum cron | Us |
| Translation volume | 490,000+ strings | Starting | Them |
| Community | Active Russian community | Solo developer | Them (for now) |
