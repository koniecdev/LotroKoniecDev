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
SETUP:
  1. CLI export        DAT -> exported.txt (angielskie teksty)
  2. Web import        exported.txt -> baza danych (POST /api/v1/db-update)
  3. Web app           tlumacze side-by-side EN/PL, review, approve
  4. Web export        baza -> polish.txt (GET /api/v1/translations/export)
  5. CLI patch         polish.txt -> DAT

CODZIENNA GRA (bez update gry):
  6. CLI launch        attrib +R -> TurbineLauncher.exe -> attrib -R
                       DAT chroniony, tlumaczenia przetrwaja

UPDATE GRY:
  7. Forum checker     wykrywa nowy post "Update XX.X Release Notes"
  8. DAT vnum check    potwierdza ze user faktycznie zainstalowal update
  9. CLI odmawia       launch zablokowany dopoki wersje sie nie zgadza
  10. User odpala      oficjalny launcher NORMALNIE (attrib -R, DAT writable)
  11. CLI patch        ponowne nalozenie tlumaczen
  12. Powrot do        kroku 6
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

| # | Co zrobic | Priority | Depends On |
|---|-----------|----------|------------|
| 1 | Rozdzielic TFM — per-project zamiast globalnego net10.0-windows/x86 | **CRITICAL** | — |
| 2 | Dodac MediatR | High | — |
| 3 | Zaprojektowac IProgress<T> dla handlerow | High | — |
| 4 | ExportTextsQuery + Handler (zastepuje Exporter) | High | #2, #3 |
| 5 | ApplyPatchCommand + Handler (zastepuje Patcher) | High | #2, #3 |
| 6 | PreflightCheckQuery + Handler (dane, zero Console I/O) | Medium | #2 |
| 7 | LoggingPipelineBehavior | Medium | #2 |
| 8 | ValidationPipelineBehavior | Medium | #2 |
| 9 | Refaktor CLI Program.cs na IMediator.Send() | High | #4, #5 |
| 10 | Usunac stare serwisy (IExporter, IPatcher, statyczne komendy) | High | #9 |
| 11 | Zaktualizowac DI (AddMediatR, behaviors) | High | #2 |
| 12 | Ogarnac ArgsOrder/ArgsId (podlaczyc albo usunac) | Medium | — |
| 13 | Ogarnac pole approved w uzyciu | Medium | — |
| 14 | **IDatVersionReader** — eksponowac vnumDatFile/vnumGameData z OpenDatFileEx2 | High | — |
| 15 | **Naprawic GameUpdateChecker** — nie zapisuj wersji forum od razu; potwierdz vnum z DAT | **CRITICAL** | #14 |
| 16 | **LaunchGameCommand** — przeniesienie lotro.bat do C# (attrib +R, Process.Start, attrib -R) | High | #2, #14, #15 |
| 17 | **IDatFileProtector** — attrib +R/-R abstrakcja | High | — |
| 18 | **IGameLauncher** — Process.Start TurbineLauncher, auto-detect sciezki | High | — |
| 19 | **Orchestracja launch**: forum check -> jesli update: zablokuj i poinformuj; jesli ok: protect+launch | High | #15, #16, #17, #18 |
| 20 | Testy jednostkowe dla handlerow | High | #4, #5 |
| 21 | Testy integracyjne | Medium | #20 |

**Po M1:** CLI dziala z komendami: `export`, `patch`, `launch`. Game update detection
uzywa dwoch zrodel (forum + DAT vnum). Launch chroniony attrib +R.

---

## M2: Baza danych

PostgreSQL (Docker) + EF Core. Import/export przez handlery. Multi-language schema.
Glossary od dnia 1. Import kontekstu z LOTRO Companion.

| # | Co zrobic | Priority | Depends On |
|---|-----------|----------|------------|
| 22 | Dodac EF Core + Npgsql (PostgreSQL) NuGet | High | #1 |
| 23 | Zaprojektowac entities (TranslationEntity vs Domain.Translation) | High | — |
| 24 | AppDbContext + konfiguracja PostgreSQL | High | #22, #23 |
| 25 | ITranslationRepository | High | M1 |
| 26 | IExportedTextRepository | High | M1 |
| 27 | Implementacja repozytoriow | High | #24-#26 |
| 28 | Migracje EF + auto-migrate w ustawieniach dev | Medium | #24 |
| 29 | ImportExportedTextsCommand + Handler (DAT export -> DB) | High | #26, #27 |
| 30 | Translation CRUD (Commands/Queries, language-aware) | High | #25, #27 |
| 31 | ExportTranslationsQuery + Handler (DB -> polish.txt) | High | #25, #27 |
| 32 | Migracja istniejacego polish.txt do bazy | Medium | #27, #30 |
| 33 | Parser exported.txt | High | #29 |
| 34 | Obsluga separatora \|\| w tresci | Medium | #30 |
| 35 | Seed jezyka polskiego | Medium | #24 |
| 36 | **GlossaryTerms entity + repository** | Medium | #24 |
| 37 | **Glossary CRUD handler** | Medium | #36 |
| 38 | **DatVersions entity** — przechowywanie historii wersji DAT/forum | Medium | #24 |
| 39 | **TextContexts entity + repository** | High | #24 |
| 40 | **LOTRO Companion XML parser** — parsowanie quests.xml, deeds.xml, NPCs.xml itd. | High | — |
| 41 | **ImportContextCommand + Handler** (Companion XML -> TextContexts) | High | #39, #40 |
| 42 | **docker-compose: PostgreSQL** | Medium | #24 |
| 43 | Testy | High | #27, #30, #41 |

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
| 50 | Przegladarka questow/deedow (grupuj po quest, NPC, region) | Medium | #46 |
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
