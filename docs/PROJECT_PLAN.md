# LOTRO Polish Patcher - Plan

## Co mamy teraz

- CLI export/patch dziala, tlumaczenia w grze smigaja
- Launcher z update checking
- ~550 test assertions (unit + integration)
- Clean Architecture (5 warstw), Result monad, P/Invoke
- Brak MediatR — komendy to statyczne klasy
- Tlumaczenia w pliku pipe-delimited — edycja uciazliwa
- Pola `approved` i `args_order` w formacie pliku, ale parser/patcher je ignoruja (dead code)
- Single-user — tylko ja tlumacze

## Cel

Lokalna aplikacja webowa do tlumaczen. Odpalam, otwieram przegladarke, tlumacze.
Zadnego auth, zadnych uslug zewnetrznych. MSSQL lokalnie (Docker lub LocalDB).

## Jak to ma dzialac

```
1. CLI export     DAT -> exported.txt (angielskie teksty)
2. Web import     exported.txt -> baza danych
3. Web            tlumacze side-by-side EN/PL
4. Web export     baza -> polish.txt
5. CLI patch      polish.txt -> DAT
6. Gram
```

## Architektura

```
┌─────────────────────────────────┐
│  Blazor SSR (localhost:5000)   │
└───────────────┬─────────────────┘
                │ MediatR
┌───────────────▼─────────────────┐
│  Application (handlery)         │
├─────────────────────────────────┤
│  Domain (modele, Result, Errors)│
├────────────────┬────────────────┤
│ Persistence    │ DatFile        │
│ EF Core, MSSQL │ P/Invoke       │
└────────────────┴────────────────┘

CLI uzywa tych samych handlerów co Web App.
```

### Projekty w solution

| Projekt | TFM | Platform | Opis |
|---------|-----|----------|------|
| Primitives | `net10.0` | AnyCPU | Stale, enumy |
| Domain | `net10.0` | AnyCPU | Modele, Result, Errors |
| Application | `net10.0` | AnyCPU | MediatR handlers, abstrakcje |
| Infrastructure.Persistence | `net10.0` | AnyCPU | EF Core, MSSQL, repozytoria |
| Infrastructure.DatFile | `net10.0-windows` | x86 | P/Invoke, datexport.dll |
| CLI | `net10.0-windows` | x86 | Presentation: CLI |
| WebApp | `net10.0` | AnyCPU | Presentation: Blazor SSR |

Obecny `Directory.Build.props` wymusza `net10.0-windows` + `x86` globalnie — trzeba
przejsc na per-project. Infrastructure trzeba rozszczepi na .DatFile i .Persistence,
inaczej TFM mismatch blokuje Web App.

## Baza danych

MSSQL przez Docker lub LocalDB. Multi-language schema od dnia 1 (aktywnie: Polish).

```sql
Languages (Code PK, Name, IsActive)
ExportedTexts (Id, FileId, GossipId, EnglishContent, Tag, ImportedAt)
  UNIQUE (FileId, GossipId)
Translations (Id, FileId, GossipId, LanguageCode FK, Content, ArgsOrder, ArgsId, IsApproved, Notes, CreatedAt, UpdatedAt)
  UNIQUE (FileId, GossipId, LanguageCode)
```

Dwa modele "Translation":
- `Domain.Models.Translation` — init-only DTO dla DAT pipeline (FileId, GossipId, Content, `int[]?` ArgsOrder)
- `Persistence.Entities.TranslationEntity` — DB entity (Id, LanguageCode, timestamps, `string` ArgsOrder)
- Mapping w repository

---

## M1: Porzadki w CLI (MediatR)

Refaktor CLI na MediatR. Kod staje sie utrzymywalny, handlery beda wspoldzielone z web app.

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
      PreflightCheckQuery.cs           : IRequest<Result<PreflightReport>>
      PreflightCheckQueryHandler.cs    : IRequestHandler
  Behaviors/
    LoggingPipelineBehavior.cs         : IPipelineBehavior
    ValidationPipelineBehavior.cs      : IPipelineBehavior

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
| 14 | Testy jednostkowe dla handlerow | High | #4, #5 |
| 15 | Testy integracyjne | Medium | #14 |

**Po M1:** CLI dziala jak wczesniej, ale kod jest czysty.

---

## M2: Baza danych

MSSQL + EF Core. Import/export przez handlery. Multi-language schema.

| # | Co zrobic | Priority | Depends On |
|---|-----------|----------|------------|
| 16 | Dodac EF Core + SQL Server NuGet | High | #1 |
| 17 | Zaprojektowac entities (TranslationEntity vs Domain.Translation) | High | — |
| 18 | AppDbContext + konfiguracja | High | #16, #17 |
| 19 | ITranslationRepository | High | M1 |
| 20 | IExportedTextRepository | High | M1 |
| 21 | Implementacja repozytoriow | High | #18-#20 |
| 22 | Migracje EF + auto-migrate w ustawieniach dev | Medium | #18 |
| 23 | ImportExportedTextsCommand + Handler | High | #20, #21 |
| 24 | Translation CRUD (Commands/Queries, language-aware) | High | #19, #21 |
| 25 | ExportTranslationsQuery + Handler (DB -> polish.txt) | High | #19, #21 |
| 26 | Migracja istniejacego polish.txt do bazy | Medium | #21, #24 |
| 27 | Parser exported.txt | High | #23 |
| 28 | Obsluga separatora \|\| w tresci | Medium | #24 |
| 29 | Seed jezyka polskiego | Medium | #18 |
| 30 | Testy | High | #21, #24 |

**Po M2:** Baza dziala, moge importowac/exportowac przez handlery.

---

## M3: Aplikacja webowa

Blazor SSR. Lista, edytor, import, export. Bez auth — single-user.

| # | Co zrobic | Priority | Depends On |
|---|-----------|----------|------------|
| 31 | Stworzyc projekt Blazor SSR | High | #1 |
| 32 | Layout i nawigacja (Bootstrap) | High | #31 |
| 33 | DI: MediatR, EF Core, DbContext | High | #31 |
| 34 | Lista tlumaczen (tabela, szukaj, filtruj, sortuj, paginacja) | High | #33 |
| 35 | Edytor tlumaczen (side-by-side EN/PL) | High | #33 |
| 36 | Podswietlanie `<--DO_NOT_TOUCH!-->` i walidacja `||` | Medium | #35 |
| 37 | Przegladarka plikow (grupuj po FileId, szukaj) | Medium | #33 |
| 38 | Dashboard (postep, ostatnie edycje) | Medium | #33 |
| 39 | Import (upload exported.txt) | Medium | #33 |
| 40 | Export (pobierz polish.txt) | Medium | #33 |
| 41 | Obsluga bledow (Result -> komunikaty) | Medium | #31 |
| 42 | Docker (docker-compose: Web App + MSSQL) | Low | #31 |
| 43 | Auto-migrate + seed przy starcie | Medium | #22 |
| 44 | Testy | High | #34, #35 |

**Po M3:** Tlumacze w przegladarce.

---

## Podsumowanie

```
M1: #1-#15   Porzadki w CLI (MediatR)
M2: #16-#30  Baza danych
M3: #31-#44  Aplikacja webowa
```

**44 issues.** Po M3 moge tlumaczyc w przegladarce.

Issue #1 (TFM split) blokuje M2+. Zaczynaj od niego.

Kazdy milestone deployowalny osobno:
- Po M1 -> CLI dziala identycznie
- Po M2 -> baza gotowa, testy przechodza
- Po M3 -> tlumacze w przegladarce

## Podjete decyzje

| Decyzja | Wybor |
|---------|-------|
| Baza danych | **MSSQL** (code-first EF Core) |
| Frontend | **Blazor SSR** |
| Architektura | MediatR handlers wspoldzielone CLI + Web |
| Auth | Brak — single-user, dodane pozniej gdy potrzeba |
| Multi-language | Schema ready od dnia 1, aktywnie tylko Polish |

---

## Pozniej (gdy bedzie potrzeba)

- **Auth (OpenIddict)** — gdy pojawi sie community (Users, UserLanguageRoles, JWT, role per jezyk)
- **Web API** — osobny REST layer gdy potrzebny remote access
- **Multi-user** — SubmittedById, ApprovedById, review workflow
- **Glossary** — slownik terminow per jezyk (EnglishTerm -> TranslatedTerm)
- **TranslationHistory** — historia edycji (OldContent, NewContent, ChangedAt)
- **AI review** — LLM sprawdza placeholders, grammar, terminologie
- **Powiadomienia** — Discord, email
- **BAT workflow** — sync.bat, patch.bat, run.bat, full-workflow.bat
