# LOTRO Polish Patcher - Plan

## Co mamy teraz

- CLI export/patch dziala, tlumaczenia w grze smigaja
- Game update checker (forum scraping + wersja w pliku tekstowym)
- `lotro.bat` z `attrib +R` — ochrona DAT przed nadpisaniem przez launcher
- `patch.bat`, `export.bat` — workflow w .bat
- ~550 test assertions (unit + integration)
- Clean Architecture (5 warstw), Result monad, P/Invoke
- Brak MediatR — komendy to statyczne klasy
- Tlumaczenia w pliku pipe-delimited — edycja uciazliwa
- Pola `approved` i `args_order` w formacie pliku, ale parser/patcher je ignoruja (dead code)
- Single-user — tylko ja tlumacze

## Cel

Platforma do polskiego tlumaczenia LOTRO. Trzy czesci:

1. **Patcher CLI** — eksport/patch DAT, ochrona plikow, launch gry (OSS)
2. **Web app** — platforma tlumaczen dla community (OSS kod, kontrolowane tlumaczenia)
3. **Desktop app** — `LotroPoPolsku.exe` (WPF) — GUI dla graczy: patch + launch jednym klikiem

Model sprawdzony przez rosyjski projekt translate.lotros.ru (490k+ stringow przetlumaczonych).
Nasz jest architektonicznie lepszy (Clean Architecture, Result monad, testy, shared handlers).

## Pelny workflow (cel koncowy)

```
SETUP (jednorazowy, tlumacz/dev):
  1. CLI export             DAT -> exported.txt (aktualne angielskie teksty)
  2. Web import tekstow     exported.txt -> baza (POST /api/v1/db-update)
  3. Web import kontekstu   LOTRO Companion XML -> baza (quests, deeds, NPCs, ...)
                            Tlumacz widzi: "Dialog Elladana, quest 'X', Shire, lvl 7"
  4. Web app                tlumacze side-by-side EN/PL z kontekstem, review, approve
  5. Web export             baza -> polish.txt (GET /api/v1/translations/export)
  6. CLI patch              polish.txt -> DAT (testowanie tlumaczen lokalnie)

CODZIENNA GRA — GRACZ (LotroPoPolsku.exe):
  7. Gracz pobiera          LotroPoPolsku.exe (jednorazowo)
  8. Klika "Patchuj"        exe pobiera polish.txt z web API, patchuje DAT
  9. Klika "Graj"           exe: attrib +R -> TurbineLauncher.exe -> attrib -R
                            DAT chroniony, tlumaczenia przetrwaja

CODZIENNA GRA — POWER USER (CLI):
  10. CLI patch polish      patchuje DAT z lokalnego pliku
  11. CLI launch            attrib +R -> TurbineLauncher.exe -> attrib -R

UPDATE GRY:
  12. Forum checker         wykrywa nowy post "Update XX.X Release Notes"
  13. DAT vnum check        potwierdza ze user faktycznie zainstalowal update
  14. Exe/CLI odmawia       launch zablokowany dopoki wersje sie nie zgadza
  15. User odpala           oficjalny launcher NORMALNIE (attrib -R, DAT writable)
  16. Re-patch              ponowne nalozenie tlumaczen (exe lub CLI)
  17. Powrot do             kroku 9 lub 11

PIPELINE — KTO CO URUCHAMIA:
  - CLI export/patch:       LOKALNIE na PC z LOTRO (datexport.dll = x86 Windows)
  - Web app:                LOKALNIE (localhost:5000) lub serwer (docker-compose)
  - LotroPoPolsku.exe:      LOKALNIE na PC gracza (pobiera polish.txt z web API, patchuje DAT)
  - Transfer:               exported.txt uploadowany przez web UI lub API
  - Cel:                    CLI/exe nigdy nie potrzebuja bazy, web nigdy nie potrzebuje datexport.dll
```

## Architektura

```
┌──────────┐  ┌──────────────────────┐  ┌────────────────────────┐
│ CLI      │  │ Blazor SSR           │  │ LotroPoPolsku.exe      │
│ (power   │  │ (localhost:5000)     │  │ (WPF, ikonka           │
│  users)  │  │ (tlumacze)           │  │  pierscienia, gracze)  │
└────┬─────┘  └──────────┬───────────┘  └───────────┬────────────┘
     │                   │                          │
     └───────────────────┼──────────────────────────┘
                         │ IMediator.Send()
              ┌──────────▼───────────┐
              │  Application         │  <- Shared handlers
              │  (MediatR handlery)  │     (zero duplikacji)
              ├──────────────────────┤
              │  Domain              │
              │  (modele, Result)    │
              ├───────────┬──────────┤
              │Persistence│ DatFile  │
              │EF Core    │ P/Invoke │  <- Infrastructure split
              └───────────┴──────────┘

Kazda warstwa prezentacji (CLI, Web, WPF) uzywa tych samych handlerow.
```

### Projekty w solution

| Projekt | TFM | Platform | Opis |
|---------|-----|----------|------|
| Primitives | `net10.0` | AnyCPU | Stale, enumy |
| Domain | `net10.0` | AnyCPU | Modele, Result, Errors |
| Application | `net10.0` | AnyCPU | MediatR handlers, abstrakcje |
| Infrastructure.Persistence | `net10.0` | AnyCPU | EF Core, PostgreSQL, repozytoria |
| Infrastructure.DatFile | `net10.0-windows` | x86 | P/Invoke, datexport.dll |
| CLI | `net10.0-windows` | x86 | Presentation: CLI (power users, automatyzacja) |
| WebApp | `net10.0` | AnyCPU | Presentation: Blazor SSR (tlumacze) |
| DesktopApp | `net10.0-windows` | x86 | Presentation: WPF (gracze, end-users) |

Obecny `Directory.Build.props` wymusza `net10.0-windows` + `x86` globalnie — trzeba
przejsc na per-project. Infrastructure trzeba rozszczepi na .DatFile i .Persistence,
inaczej TFM mismatch blokuje Web App.

## Baza danych

PostgreSQL przez Docker. Multi-language schema od dnia 1 (aktywnie: Polish).

### Zrodla danych

Dwa niezalezne zrodla:
1. **DAT export** (CLI `export`) — aktualne angielskie teksty z gry (file_id, gossip_id, content)
2. **LOTRO Companion** (https://github.com/LotroCompanion/lotro-data) — kontekst/metadane:
   quests.xml, deeds.xml, NPCs.xml, titles.xml, skills.xml itd.

LOTRO Companion uzywa formatu `key:{file_id}:{gossip_id}` — ID zgadzaja sie 1:1 z naszym exportem.
Dzieki temu tlumacz widzi kontekst: "Dialog Elladana w quecie 'The Bird and Baby' (Shire, lvl 7)"
zamiast golego `620871150||218649169||tekst`.

### Schema

```sql
Languages (Code PK, Name, IsActive)

-- Aktualne teksty z gry (zrodlo: DAT export)
ExportedTexts (Id, FileId, GossipId, EnglishContent, ImportedAt)
  UNIQUE (FileId, GossipId)

-- Kontekst z LOTRO Companion (zrodlo: quests.xml, deeds.xml, NPCs.xml itd.)
TextContexts (Id, FileId, GossipId, ContextType, ParentName, ParentCategory,
              ParentLevel, NpcName, Region, SourceFile, ImportedAt)
  UNIQUE (FileId, GossipId, ContextType)
  -- ContextType: QuestName, QuestDescription, QuestBestowerText, QuestObjective,
  --              QuestDialog, DeedName, DeedDescription, NpcDialog, SkillName,
  --              TitleName, ItemName, ...

Translations (Id, FileId, GossipId, LanguageCode FK, Content, ArgsOrder, ArgsId,
              IsApproved, Notes, CreatedAt, UpdatedAt)
  UNIQUE (FileId, GossipId, LanguageCode)

TranslationHistory (TranslationId, OldContent, NewContent, ChangedAt)

GlossaryTerms (Id, EnglishTerm, PolishTerm, Notes, Category, CreatedAt)
  UNIQUE (EnglishTerm, Category)

DatVersions (Id, VnumDatFile, VnumGameData, ForumVersion, DetectedAt)
```

Dwa modele "Translation":
- `Domain.Models.Translation` — init-only DTO dla DAT pipeline (FileId, GossipId, Content, `int[]?` ArgsOrder)
- `Persistence.Entities.TranslationEntity` — DB entity (Id, LanguageCode, timestamps, `string` ArgsOrder)
- Mapping w repository

---

## M1: Porzadki w CLI (MediatR + Launch + Update Fix)

Refaktor CLI na MediatR. Handlers wspoldzielone z web app.
Dodanie komendy launch. Naprawa game update detection.

Handlers **zastepuja** serwisy (Exporter, Patcher). `IExporter`/`IPatcher` -> usuniete.
Progress via `IProgress<T>` w DI — nie callback. PreflightCheckQuery zwraca dane — zero `Console.ReadLine()`.

**Struktura po M1:**
```
Application/
  Features/
    Export/
      ExportTextsQuery.cs              : IRequest<Result<ExportSummary>>
      ExportTextsQueryHandler.cs       : IRequestHandler
    Patch/
      ApplyPatchCommand.cs             : IRequest<Result<PatchSummary>>
      ApplyPatchCommandHandler.cs      : IRequestHandler
    Preflight/
      PreflightCheckQuery.cs           : IRequest<Result<PreflightReport>>
      PreflightCheckQueryHandler.cs    : IRequestHandler
    Launch/
      LaunchGameCommand.cs             : IRequest<Result>
      LaunchGameCommandHandler.cs      : IRequestHandler
    UpdateCheck/
      GameUpdateCheckQuery.cs          : IRequest<Result<GameUpdateStatus>>
      GameUpdateCheckQueryHandler.cs   : IRequestHandler
  Behaviors/
    LoggingPipelineBehavior.cs         : IPipelineBehavior
    ValidationPipelineBehavior.cs      : IPipelineBehavior

Abstractions (nowe):
  IDatVersionReader.cs               Czyta vnumDatFile/vnumGameData z DAT
  IGameLauncher.cs                   Odpala TurbineLauncher z flagami
  IDatFileProtector.cs               attrib +R/-R na DAT

Usuniete:
  IExporter, IPatcher, Exporter, Patcher,
  PreflightChecker, ExportCommand, PatchCommand (static classes)
```

M1 jest duzy — 3 odrebne tematy. Wykonuj w fazach, kazda faza zamknieta (testy przechodza):

### Faza A: TFM split + MediatR setup (fundament)

| # | Co zrobic | Priority | Depends On |
|---|-----------|----------|------------|
| 1 | Rozdzielic TFM — per-project zamiast globalnego net10.0-windows/x86 | **CRITICAL** | — |
| 2 | Dodac MediatR + AddMediatR do DI | High | — |
| 3 | Zaprojektowac IProgress<T> dla handlerow | High | — |

**Checkpoint A:** Build przechodzi, testy przechodza, TFM per-project. MediatR zarejestrowany ale jeszcze nie uzywany.

### Faza B: MediatR handlers (zastepuja serwisy)

Strategia migracji: nowe handlery obok starych serwisow -> CLI przechodzi na handlery -> stare serwisy usuwane. Testy przechodza na KAZDYM etapie.

| # | Co zrobic | Priority | Depends On |
|---|-----------|----------|------------|
| 4 | ExportTextsQuery + Handler (nowy, obok starego Exporter) | High | #2, #3 |
| 5 | ApplyPatchCommand + Handler (nowy, obok starego Patcher) | High | #2, #3 |
| 6 | PreflightCheckQuery + Handler (dane, zero Console I/O) | Medium | #2 |
| 7 | LoggingPipelineBehavior | Medium | #2 |
| 8 | ValidationPipelineBehavior | Medium | #2 |
| 9 | Refaktor CLI Program.cs na IMediator.Send() | High | #4, #5 |
| 10 | Usunac stare serwisy (IExporter, IPatcher, statyczne komendy) | High | #9 |
| 11 | Testy jednostkowe dla handlerow | High | #4, #5 |
| 12 | Testy integracyjne | High | #11 |

**Checkpoint B:** CLI uzywa MediatR. Stare serwisy usuniete. Wszystkie testy przechodza.

### Faza C: Launch command + update fix

| # | Co zrobic | Priority | Depends On |
|---|-----------|----------|------------|
| 13 | **IDatVersionReader** — eksponowac vnumDatFile/vnumGameData z OpenDatFileEx2 | High | — |
| 14 | **Naprawic GameUpdateChecker** — nie zapisuj wersji forum od razu; potwierdz vnum z DAT | **CRITICAL** | #13 |
| 15 | **IDatFileProtector** — attrib +R/-R abstrakcja + impl | High | — |
| 16 | **IGameLauncher** — Process.Start TurbineLauncher, auto-detect sciezki | High | — |
| 17 | **LaunchGameCommand + Handler** — przeniesienie lotro.bat do C# | High | #2, #13, #14, #15, #16 |
| 18 | **Orchestracja launch**: forum check -> update? blokuj : protect+launch | High | #14, #17 |
| 19 | Testy dla launch + update detection | High | #17, #18 |

**Checkpoint C:** CLI: `export`, `patch`, `launch`. Update detection 2-warstwowa. Testy ok.

### Faza D: Cleanup (opcjonalna, moze isc rownolegle z M2)

| # | Co zrobic | Priority | Depends On |
|---|-----------|----------|------------|
| 20 | ArgsOrder/ArgsId — podlaczyc w Patcher (teraz ignorowane). Zostawic w uzyciu, potrzebne do reorderingu argumentow w tlumaczeniach. | Medium | — |
| 21 | Pole `approved` — zostawic w formacie pliku, ignorowac w CLI. Bedzie uzywane dopiero w M2 (DB: IsApproved). | Low | — |

**Po M1:** CLI dziala z komendami: `export`, `patch`, `launch`. Game update detection
uzywa dwoch zrodel (forum + DAT vnum). Launch chroniony attrib +R.

---

## M2: Baza danych

PostgreSQL (Docker) + EF Core. Import/export przez handlery. Multi-language schema.
Glossary od dnia 1. Import kontekstu z LOTRO Companion.

M2 wymaga tylko TFM split (#1) z M1. Nie wymaga MediatR — repozytoria to czysty EF Core.
Moze byc robiony rownolegle z M1 Faza C/D.

### Faza A: PostgreSQL + EF Core setup

| # | Co zrobic | Priority | Depends On |
|---|-----------|----------|------------|
| 22 | **docker-compose: PostgreSQL** (bez tego baza nie ruszy) | **CRITICAL** | — |
| 23 | Dodac EF Core + Npgsql NuGet do Infrastructure.Persistence | High | #1 |
| 24 | Zaprojektowac entities (TranslationEntity vs Domain.Translation) | High | — |
| 25 | AppDbContext + konfiguracja PostgreSQL | High | #22, #23, #24 |
| 26 | Migracje EF + auto-migrate w dev | High | #25 |
| 27 | Seed jezyka polskiego | Medium | #25 |

**Checkpoint A:** `docker-compose up` startuje PostgreSQL, migracje tworza schema, seed dziala.

### Faza B: Repozytoria + import z DAT export

| # | Co zrobic | Priority | Depends On |
|---|-----------|----------|------------|
| 28 | IExportedTextRepository + impl | High | #25 |
| 29 | ITranslationRepository + impl | High | #25 |
| 30 | Parser exported.txt (reuse istniejacego TranslationFileParser lub nowy) | High | — |
| 31 | ImportExportedTextsCommand + Handler (exported.txt -> DB) | High | #28, #30 |
| 32 | Translation CRUD (Commands/Queries, language-aware) | High | #29 |
| 33 | ExportTranslationsQuery + Handler (DB -> polish.txt) | High | #29 |
| 34 | Migracja istniejacego polish.txt do bazy | Medium | #29, #32 |
| 35 | Obsluga separatora \|\| w tresci (escaping) | Medium | #32 |

**Checkpoint B:** Moge zaimportowac exported.txt i polish.txt do bazy, wyexportowac polish.txt z bazy.

### Faza C: LOTRO Companion kontekst + glossary

| # | Co zrobic | Priority | Depends On |
|---|-----------|----------|------------|
| 36 | TextContexts entity + repository | High | #25 |
| 37 | **LOTRO Companion XML parser** — parsowanie quests.xml, deeds.xml, NPCs.xml itd. | High | — |
| 38 | **ImportContextCommand + Handler** (Companion XML -> TextContexts) | High | #36, #37 |
| 39 | GlossaryTerms entity + repository | Medium | #25 |
| 40 | Glossary CRUD handler | Medium | #39 |
| 41 | DatVersions entity — przechowywanie historii wersji DAT/forum | Medium | #25 |

**Checkpoint C:** Companion XML zaimportowany, kazdy (FileId, GossipId) ma kontekst. Glossary ready.

### Faza D: Testy

| # | Co zrobic | Priority | Depends On |
|---|-----------|----------|------------|
| 42 | Testy unit (repozytoria, parsery, handlery) | High | #31, #32, #38 |
| 43 | Testy integracyjne (pelen pipeline: import -> CRUD -> export) | High | #42 |

**Po M2:** Baza dziala, importy z DAT + Companion, kontekst przy kazdym stringu. Glossary gotowy.

---

## M3: Aplikacja webowa

Blazor SSR. Lista, edytor, glossary, import, export. Bez auth — single-user na start.
Tlumacz widzi kontekst z LOTRO Companion (nazwa questa, NPC, region, level).

| # | Co zrobic | Priority | Depends On |
|---|-----------|----------|------------|
| 44 | Stworzyc projekt Blazor SSR | High | #1 |
| 45 | Layout i nawigacja (Bootstrap) | High | #44 |
| 46 | DI: MediatR, EF Core, DbContext | High | #44 |
| 47 | Lista tlumaczen (tabela, szukaj, filtruj, sortuj, paginacja) | High | #46 |
| 48 | Edytor tlumaczen (side-by-side EN/PL + kontekst z Companion) | High | #46 |
| 49 | Podswietlanie `<--DO_NOT_TOUCH!-->` i walidacja placeholderow | Medium | #48 |
| 50 | Przegladarka questow/deedow (grupuj po quest, NPC, region) | Medium | #46, M2#36 |
| 51 | Dashboard (postep, ostatnie edycje, statystyki) | Medium | #46 |
| 52 | Import (upload exported.txt) / API endpoint POST /api/v1/db-update | Medium | #46 |
| 53 | Export (pobierz polish.txt) / API endpoint GET /api/v1/translations/export | Medium | #46 |
| 54 | **Glossary UI** — przegladanie, dodawanie, szukanie terminow | Medium | #46 |
| 55 | **Style guide page** — zasady tlumaczenia, konwencje Tolkienowskie | Low | #44 |
| 56 | Obsluga bledow (Result -> komunikaty) | Medium | #44 |
| 57 | Keyboard shortcuts (Ctrl+S save, Ctrl+Enter save+next, Ctrl+Shift+Enter approve+next) | Medium | #48 |
| 58 | Bulk operations — zaznacz wiele, zatwierdz/odrzuc batch | Medium | #47 |
| 59 | Responsive design (tablet, mobile) | Low | #45 |
| 60 | Docker (docker-compose: Web App + PostgreSQL) | Low | #44, #42 |
| 61 | Auto-migrate + seed przy starcie | Medium | #28 |
| 62 | Testy | High | #47, #48 |

**Po M3:** Tlumacze w przegladarce z kontekstem. Glossary, style guide, review workflow.

---

## M4: Desktop App — LotroPoPolsku.exe

WPF app dla graczy (end-userow). Ikonka pierscienia. Jedno klikniecie = gra po polsku.
Uzywa tych samych MediatR handlerow co CLI i Web — zero duplikacji.

**Dlaczego WPF a nie CLI dla graczy:**
- Gracz nie chce widziec konsoli
- Ikonka na pulpicie, splash screen, progress bar
- Auto-update (jak rosyjski Jozo/Elanor)
- "Pobierz, kliknij, graj" — zero konfiguracji

**WPF NIE zastepuje CLI.** CLI zostaje dla:
- Power userow i automatyzacji
- CI/CD pipeline (export -> web import)
- Debugowania i development

| # | Co zrobic | Priority | Depends On |
|---|-----------|----------|------------|
| 63 | Stworzyc projekt WPF (`net10.0-windows`, x86) | High | M1 |
| 64 | Ikona pierscienia (asset) + splash screen | Medium | #63 |
| 65 | Glowne okno: status tlumaczen, wersja gry, wersja patcha | High | #63 |
| 66 | Przycisk "Patchuj" -> `IMediator.Send(new ApplyPatchCommand(...))` | High | #63, M1 |
| 67 | Przycisk "Graj" -> `IMediator.Send(new LaunchGameCommand(...))` | High | #63, M1 |
| 68 | Progress bar + status (IProgress<T>) | High | #63 |
| 69 | Auto-detekcja LOTRO (IDatFileLocator) — zero konfiguracji | High | #63 |
| 70 | Game update alert — banner "Zaktualizuj gre!" z instrukcja | High | #63, M1#15 |
| 71 | Ustawienia: sciezka LOTRO, jezyk, auto-patch on launch | Medium | #63 |
| 72 | Minimalizacja do tray (opcjonalne) | Low | #63 |
| 73 | Auto-update apki (sprawdz GitHub releases, pobierz nowa wersje) | Medium | #63 |
| 74 | Installer (MSIX lub Inno Setup) z ikonka i skrotem na pulpicie | Medium | #63 |
| 75 | Testy | High | #66, #67 |

**Po M4:** Gracz pobiera `LotroPoPolsku.exe`, klika "Graj" — gotowe.

---

## M5 (pozniej): Community & Auth

Gdy pojawi sie community — dodac auth i multi-user.

| # | Co zrobic | Priority |
|---|-----------|----------|
| 76 | Auth (OpenIddict) — Users, JWT, role | When needed |
| 77 | UserLanguageRoles — role per jezyk (translator/reviewer/admin) | When needed |
| 78 | SubmittedById, ApprovedById w Translations | When needed |
| 79 | Review workflow — submit -> review -> approve/reject | When needed |
| 80 | TranslationHistory z ChangedBy | When needed |
| 81 | AI review — LLM sprawdza placeholders, grammar, terminologie | When needed |
| 82 | Powiadomienia — Discord webhook | When needed |
| 83 | Public REST API — remote access do platformy | When needed |

---

## Podsumowanie

```
M1: #1-#21    Porzadki CLI + Launch + Update Fix
M2: #22-#43   Baza danych + Glossary + LOTRO Companion import
M3: #44-#62   Aplikacja webowa (dla tlumaczow, z kontekstem)
M4: #63-#75   Desktop app LotroPoPolsku.exe (dla graczy)
M5: #76-#83   Community & Auth (pozniej)
```

**75 issues do M4.** Po M4 mamy pelna platforme: tlumacze pracuja w webie z kontekstem, gracze odpalaja .exe.

Issue #1 (TFM split) blokuje M2+M3+M4. Zaczynaj od niego.

Kazdy milestone deployowalny osobno:
- Po M1 -> CLI: export, patch, launch. Update detection dziala poprawnie.
- Po M2 -> baza gotowa, glossary, testy przechodza
- Po M3 -> tlumacze pracuja w przegladarce
- Po M4 -> gracze pobieraja LotroPoPolsku.exe i klikaja "Graj"

## Podjete decyzje

| Decyzja | Wybor | Uzasadnienie |
|---------|-------|-------------|
| Baza danych | **PostgreSQL** (code-first EF Core + Npgsql) | Darmowy bez limitow, Docker, lepszy hosting |
| Kontekst tlumaczen | **LOTRO Companion** (lotro-data XML) | ID zgadzaja sie 1:1 z naszym DAT export |
| Web frontend | **Blazor SSR** | Ten sam C#, shared models |
| Desktop app | **WPF** (LotroPoPolsku.exe) | Natywny Windows, ikonka, end-user friendly |
| Architektura | MediatR handlers CLI + Web + WPF | Zero duplikacji — 3 UI, 1 backend |
| Auth | Brak na start, OpenIddict pozniej | Single-user -> community |
| Multi-language | Schema ready od dnia 1 | Tylko Polish aktywny |
| Ochrona DAT | `attrib +R` (OS-level) | Silniejsze niz -disablePatch |
| Detekcja update | Forum + DAT vnum (dwa zrodla) | Forum = alert, vnum = potwierdzenie |
| Kod zrodlowy | OSS | Zaufanie, devs, community |
| Tlumaczenia | Kontrolowane (review) | Jedna kanoniczna wersja |
| Glossary | Od M2 (DB) | Spójność terminow Tolkienowskich |
| CLI vs GUI | **Oba** — CLI zostaje, WPF dla graczy | CLI = power users/CI, WPF = end-users |

## Game Update Detection — szczegoly

```
DWIE WARSTWY DETEKCJI:

1. Forum checker (PROAKTYWNY ALERT)
   - Scrapuje https://forums.lotro.com/index.php?forums/release-notes-and-known-issues.7/
   - Regex: Update\s+(\d+(?:\.\d+)*)\s+Release\s+Notes
   - Porownuje z data/last_known_game_version.txt
   - Wie o updacie ZANIM user cokolwiek zrobi

2. DAT vnum (TWARDE POTWIERDZENIE)
   - OpenDatFileEx2() -> out vnumDatFile, out vnumGameData
   - Zmienia sie gdy oficjalny launcher zaktualizuje DAT
   - Potwierdza ze user FAKTYCZNIE zainstalowal update
   - Obecnie IGNOROWANE w DatFileHandler.Open() (out _) — DO NAPRAWIENIA

FLOW:
  Forum: "Jest 42.2"  + DAT vnum: stary    → "Zaktualizuj gre!" + zablokuj launch
  Forum: "Jest 42.2"  + DAT vnum: nowy     → "Zpatchuj tlumaczenia" + pozwol
  Forum: "Jest 42.1"  + DAT vnum: 42.1     → "OK, grasz" + launch dozwolony
```

## Ochrona DAT — szczegoly

```
lotro.bat (obecny):
  attrib +R "client_local_English.dat"     # Read-only = launcher nie nadpisze
  start /wait "" "LotroLauncher.exe"        # Czeka az launcher sie zamknie
  attrib -R "client_local_English.dat"     # Przywraca zapis

Dlaczego lepsze niz rosyjskie -disablePatch:
  - attrib +R = blokada na poziomie OS, launcher NIE MOZE jej obejsc
  - -disablePatch = undokumentowana flaga, SSG moze usunac w kazdym updacie
  - attrib +R blokuje tez update gry — to jest CELOWE (wymusza explicit update flow)

Do przeniesienia do C# w M1 (#16, #17, #18):
  IDatFileProtector.Protect(path)    -> attrib +R
  IDatFileProtector.Unprotect(path)  -> attrib -R
  IGameLauncher.Launch(path)         -> Process.Start + wait
```

## Porownanie z rosyjskim projektem (translate.lotros.ru)

```
WSPOLNE DNA:
  - datexport.dll (ten sam P/Invoke)
  - TextFileMarker = 0x25
  - Format: file_id||gossip_id||content||args
  - Pipeline: export DAT -> baza -> web UI -> tlumacz -> export -> patch DAT

NASZE PRZEWAGI:
  - Clean Architecture (5 warstw) vs ich monolit
  - Result monad vs try/catch
  - ~550 test assertions vs brak
  - Shared MediatR handlers (CLI + Web) vs duplikacja
  - attrib +R (OS-level) vs -disablePatch (undocumented flag)
  - Proaktywna detekcja update (forum) vs reaktywna (NinjaMark)
  - Multi-language schema od dnia 1 vs hardcoded "Ru"

ICH PRZEWAGI (na dzis):
  - Dzialajaca web platforma z 490k+ stringow
  - Aktywna community tlumaczow
  - Lata doswiadczenia z pipeline
  - Modularnosc patchy (text, font, image, sound, texture, loadscreen)

NASZ DOCELOWY STACK (po M4):
  CLI (export/patch/launch)  — power users, CI, automatyzacja
  Web App (Blazor SSR)       — platforma tlumaczen, glossary, review
  Desktop (WPF)              — LotroPoPolsku.exe, "pobierz i kliknij Graj"
  Wszystko uzywa tych samych MediatR handlerow — zero duplikacji
```

---

## Krok po kroku — co robic i kiedy

Kolejnosc wykonania z checkpointami. Kazdy krok konczy sie dzialajacym buildem + testami.

```
=== M1 FAZA A: Fundament (blokuje wszystko) ===

  [ ] #1  Rozdziel TFM per-project
          - Directory.Build.props: usun net10.0-windows i x86
          - Kazdy .csproj definiuje wlasny TFM
          - Infrastructure.DatFile + CLI: net10.0-windows, x86
          - Reszta: net10.0, AnyCPU
          - Zweryfikuj: dotnet build, dotnet test — przechodzi
          CHECKPOINT: build + testy ok

  [ ] #2  Dodaj MediatR
          - NuGet: MediatR + MediatR.Extensions.Microsoft.DependencyInjection
          - services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(...))
          - Jeszcze nie uzywany — tylko rejestracja
          CHECKPOINT: build ok, MediatR wstrzykniety

  [ ] #3  Zaprojektuj IProgress<T>
          - Typ postpu (np. OperationProgress record)
          - CLI: ConsoleProgressReporter : IProgress<OperationProgress>
          - Pozniej Web/WPF podepna swoje implementacje

=== M1 FAZA B: MediatR handlers ===

  [ ] #4  ExportTextsQuery + Handler
          - Nowy handler OBOK istniejacego Exporter
          - Handler wywoluje te same metody co Exporter
          - Unit testy

  [ ] #5  ApplyPatchCommand + Handler
          - Nowy handler OBOK istniejacego Patcher
          - Unit testy

  [ ] #6-8 PreflightCheckQuery, LoggingBehavior, ValidationBehavior

  [ ] #9  Refaktor Program.cs
          - export -> IMediator.Send(new ExportTextsQuery(...))
          - patch -> IMediator.Send(new ApplyPatchCommand(...))
          - Zweryfikuj: CLI dziala identycznie

  [ ] #10 Usun stare serwisy
          - IExporter, IPatcher, Exporter, Patcher, ExportCommand, PatchCommand
          - Popraw testy ktore ich uzywaly

  [ ] #11-12 Testy
          CHECKPOINT: CLI na MediatR, stare serwisy usuniete, testy ok

=== M1 FAZA C: Launch + Update Fix ===

  [ ] #13 IDatVersionReader — eksponuj vnum z OpenDatFileEx2
  [ ] #14 Napraw GameUpdateChecker — nie zapisuj wersji od razu
  [ ] #15 IDatFileProtector — attrib +R/-R
  [ ] #16 IGameLauncher — Process.Start
  [ ] #17 LaunchGameCommand + Handler
  [ ] #18 Orchestracja launch (forum check -> protect -> launch)
  [ ] #19 Testy
          CHECKPOINT: `lotro patch polish`, `lotro launch` — dzialaja

=== M1 FAZA D: Cleanup (opcjonalne, moze czekac) ===

  [ ] #20 ArgsOrder/ArgsId — zostawic, dzialaja w patcherze
  [ ] #21 approved — zostawic w formacie, ignorowac w CLI

====================================================================

=== M2 FAZA A: PostgreSQL setup ===
    (moze startowac po #1, rownolegle z M1 Faza C)

  [ ] #22 docker-compose.yml z PostgreSQL
          - docker-compose up -> baza dziala na localhost:5432
  [ ] #23 EF Core + Npgsql NuGet w Infrastructure.Persistence
  [ ] #24 Zaprojektuj entities
  [ ] #25 AppDbContext
  [ ] #26 Migracje + auto-migrate
  [ ] #27 Seed Polish
          CHECKPOINT: docker-compose up, migracje ok, seed ok

=== M2 FAZA B: Import + CRUD ===

  [ ] #28 IExportedTextRepository
  [ ] #29 ITranslationRepository
  [ ] #30 Parser exported.txt
  [ ] #31 ImportExportedTextsCommand (exported.txt -> DB)
          - Uruchom CLI export -> exported.txt
          - Uruchom import -> baza pena angielskich tekstow
  [ ] #32 Translation CRUD
  [ ] #33 ExportTranslationsQuery (DB -> polish.txt)
  [ ] #34 Migracja polish.txt do bazy
  [ ] #35 Escaping separatora || w tresci
          CHECKPOINT: roundtrip: export DAT -> import DB -> CRUD -> export polish.txt -> patch DAT

=== M2 FAZA C: Companion + Glossary ===

  [ ] #36 TextContexts entity
  [ ] #37 LOTRO Companion XML parser
          - git clone LotroCompanion/lotro-data
          - Parsuj quests.xml (574k linii), deeds.xml, NPCs.xml
          - Wyciagnij key:{file_id}:{gossip_id} + kontekst
  [ ] #38 ImportContextCommand
  [ ] #39 GlossaryTerms entity + CRUD
  [ ] #40 Glossary handler
  [ ] #41 DatVersions entity
          CHECKPOINT: kazdy string w DB ma kontekst z Companion

=== M2 FAZA D: Testy ===

  [ ] #42-43 Unit + integration testy
          CHECKPOINT: pelen pipeline przetestowany

====================================================================

=== M3: Web App ===
    (wymaga: M1 Faza B + M2)

  [ ] #44 Blazor SSR projekt (net10.0, AnyCPU)
  [ ] #45-46 Layout, DI
  [ ] #47 Lista tlumaczen (z kontekstem z TextContexts!)
  [ ] #48 Edytor side-by-side EN/PL
  [ ] #49-62 Reszta UI features
          CHECKPOINT: localhost:5000 — tlumaczysz w przegladarce

=== M4: Desktop App ===
    (wymaga: M1)

  [ ] #63-75 WPF, LotroPoPolsku.exe
          CHECKPOINT: gracz pobiera exe, klika "Graj"
```
