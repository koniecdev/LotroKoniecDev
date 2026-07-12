# ADR-0033: Avalonia UI instead of WPF for the M4 desktop player app

**Status:** Accepted
**Date:** 2026-07-12
**Decision-makers:** Solo maintainer
**Related:** M4 milestone (epic #472; tickets #41, #42, #43, #45, #46 — all not started), ADR-0001 §2 (handler consumers), `docs/knowledge-base/russian-project.md`

## Context

M4 plans a small desktop player app — two buttons (**Patch**, **Play**) over the existing patcher
Application handlers (`Features/{Exporting,GameLaunching,Patching,PreflightChecking,
TranslationFileSyncing,UpdateChecking}`) plus the M2-20 TMS auto-download. Every M4 artifact
(epic #472, tickets #41–#46, the CLAUDE.md roadmap digest, ADR-0001 §2's "WPF view models later")
says **WPF**. Nothing is implemented yet, and there are no users — the framework choice is free
today and expensive later.

Facts that constrain the choice:

- **The hard Windows lock is native interop, not the GUI.** `DatFile/DatExportNative.cs` P/Invokes
  `datexport.dll` (x86 Windows native, shipped with `msvcp*/msvcr*/zlib1T` runtime DLLs). Patching
  requires a Windows or Wine/Proton process regardless of UI framework.
- **Steam Deck is a real audience and needs no new architecture — only a Wine-tolerant app.**
  LOTRO has no Linux build; on Deck it runs under Proton, and `client_local_English.dat` is a plain
  file inside the Proton prefix. Patching is file I/O: a Windows exe run in the game's prefix
  serves Deck users as-is. The GUI framework alone decides whether that path works.
- **WPF is the one .NET UI framework that closes the Proton path.** It renders through
  milcore/DirectX9 and is notoriously broken under Wine. Avalonia renders through Skia, behaves
  well under Wine, and additionally runs natively on Linux/macOS.
- **The Russian sister project already ran this experiment**
  (`docs/knowledge-base/russian-project.md`, `docs/RUSSIAN_PROJECT_RESEARCH.md` §3): their
  first launcher, Elanor, was **C# WPF** — exactly our current M4 plan. They paid for a full
  rewrite to C++ Qt (Legacy v2 → Legacy 3.0) and today ship Windows (primary) plus Linux/Steam
  Deck via Proton, with tested compatibility across Proton 5.13-6, 6.3-8 and GE-Proton9. Their
  Linux/Steam Deck support is listed in our own research as a lesson worth learning (§9.1.7).
- Avalonia's programming model is XAML + MVVM — near-identical developer experience to WPF for a
  two-button app, so the switch costs ~nothing at this stage.

## Decision

### 1. The M4 desktop app is Avalonia UI, not WPF

Cross-platform .NET, XAML + MVVM, Skia rendering. The epic's shape is unchanged: a GUI shell over
the existing patcher handlers + TMS auto-download, no new backend surface.

### 2. M4 MVP still targets Windows only; the Proton path must stay open

The M4 Definition of Done stays "on a Windows box". Steam Deck support (running the app inside the
game's Proton prefix, as the Russian Legacy launcher does) is an aspiration, not an M4 acceptance
criterion — but no M4 implementation choice may re-close it: no Windows-only APIs in the app
project beyond what the patcher Infrastructure already imposes, and Windows-specific bits (install
path detection via registry, elevation) live behind abstractions.

### 3. Handler consumption follows ADR-0001 unchanged

View models constructor-inject the closed `ICommandHandler<,>`/`IQueryHandler<,>` interfaces
exactly like `LotroKoniecDev.Cli` commands do. ADR-0001 §2's "WPF view models later" now reads
"Avalonia view models" — same pattern, different shell.

### 4. Native Linux is out of scope until an in-house DAT writer exists

A native Linux build cannot call `datexport.dll`. The Russians solved this with their own C++
LotroDat library; our equivalent (a managed DAT writer) is a large, separate, post-MVP effort that
gets its own ADR if it ever earns its keep. Until then, "cross-platform" means "Wine/Proton
friendly", nothing more.

### 5. Naming follows the decision

The milestone reads "M4 — desktop player app (Avalonia)"; epic #472 and its child tickets are
re-titled and re-aligned (M4-06 "WPF tests" becomes headless UI tests via `Avalonia.Headless`).

## Consequences

### Positive

- The Steam Deck/Proton path stays open with zero extra M4 scope — the exact user-base expansion
  the Russian project validated (research §9.1.7).
- If a managed DAT writer ever lands, the GUI is already cross-platform; no Elanor-style rewrite.
- `Avalonia.Headless` enables real UI tests in CI on Linux runners — WPF tests would have been
  Windows-runner-only, like the patcher E2E suite.
- Same XAML/MVVM skillset and the same ADR-0001 consumer pattern; switch cost today is ~zero.

### Negative / Accepted Trade-offs

- Third-party OSS dependency instead of a Microsoft-shipped framework — smaller ecosystem, fewer
  Stack Overflow answers, own release cadence. Accepted: Avalonia is the de-facto standard for
  cross-platform .NET desktop and is actively funded.
- No Windows-native look & feel out of the box (Fluent theme approximates it). Irrelevant for a
  two-button utility.
- "Runs under Proton" is asserted from Avalonia's Skia rendering and the Russian precedent, not
  yet proven for our exe — verifying it on a real Deck/Wine prefix becomes an explicit (post-)M4
  task instead of an assumption.

## Alternatives Considered

### A. WPF (the original plan)

Mature, Microsoft-shipped, the M4 tickets already say it. Rejected. It is the single .NET UI
choice that closes the Proton/Steam Deck path, and the Russian project's Elanor→Qt history is a
paid-for demonstration of what escaping it later costs.

### B. .NET MAUI

Microsoft's cross-platform framework. Rejected. No Linux target at all (Windows/macOS/iOS/Android
only), so it fails the exact motivation for leaving WPF; desktop is its second-class citizen.

### C. C++ Qt (the Russian endgame)

Proven by Legacy 3.0. Rejected. Abandons the whole point of M4 — reusing the patcher Application
handlers 1:1 in-process — and swaps our entire toolchain for a UI shell.

### D. WinForms

Tolerates Wine better than WPF. Rejected. Legacy technology with no cross-platform future and no
MVVM story; if we're choosing for Wine-friendliness anyway, Avalonia gives that plus native Linux.

### E. No desktop app — web frontend only

Rejected. Patching requires local file access and x86 native interop on the player's machine;
that is the entire reason M4 exists.

## Implementation Notes

Nothing is implemented now (M4 is not started); this ADR only re-points the plan:

- Epic #472 + tickets #41, #42, #43, #45, #46 — re-title "WPF" → "Avalonia", align bodies
  (incl. #46: `Avalonia.Headless` test approach).
- `CLAUDE.md` — roadmap digest and M4 mentions updated ("WPF player app" → "desktop player app
  (Avalonia)").
- ADR-0001 §2 — the "WPF view models later" wording is superseded by this ADR (0001 itself is
  otherwise untouched and stays Accepted).
- Future app project lands under `src/Player/` at M4-01; project naming is decided there.

## References

- `docs/knowledge-base/russian-project.md` + `docs/RUSSIAN_PROJECT_RESEARCH.md` §3, §9.1 —
  Elanor (WPF) → Legacy v2/3.0 (Qt), Proton compatibility matrix, lessons list
- Epic #472 (M4 tracking), tickets #41–#46
- ADR-0001 — slim SRP handlers (consumer pattern the view models follow)
- ProtonDB — LOTRO on Linux/Steam Deck: https://www.protondb.com/app/212500
- Avalonia UI: https://avaloniaui.net/
