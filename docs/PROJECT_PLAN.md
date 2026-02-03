# LOTRO Polish Patcher - Project Plan

> Od CLI do lokalnej aplikacji webowej do tłumaczeń. 3 milestones, 51 issues.

## Stan obecny (MVP)

- CLI export/patch działa, tłumaczenia w grze śmigają
- Launcher z update checking
- ~550 test assertions (unit + integration)
- Clean Architecture (5 warstw), Result monad, P/Invoke
- Brak MediatR — komendy to statyczne klasy
- Tłumaczenia w pliku pipe-delimited — edycja karkołomna
- Pola `approved` i `args_order` w formacie pliku, ale parser/patcher je ignorują (dead code)
- Single-user — ja tłumaczę, ja patchuję

## Filozofia

**Single-user local-first.** Aplikacja webowa na localhost do wygodnej pracy nad
tlumaczeniami. Zero auth. MSSQL lokalnie (Docker lub LocalDB). `dotnet run` — i do roboty.

Multi-language i multi-user w strukturach kodu (future-proofing), ale aktywny development
tylko na Polish. Auth, Web API, role — dopiero gdy pojawi się community.

## Architektura docelowa

```
┌────────────────────────────────────────────────────┐
│           Blazor SSR Web App                       │
│           (LotroKoniecDev.WebApp)                  │
│           localhost:5000, no auth                  │
└──────────────────┬─────────────────────────────────┘
                   │ MediatR (in-process)
┌──────────────────▼─────────────────────────────────┐
│  LotroKoniecDev.Application  [net10.0]             │
│  Handlers: Export, Patch, Translation CRUD         │
│  Behaviors: Logging, Validation                    │
├────────────────────────────────────────────────────┤
│  LotroKoniecDev.Domain  [net10.0]                  │
│  Models, Result<T>, Errors, VOs                    │
├────────────────────┬───────────────────────────────┤
│  Infrastructure    │  Infrastructure               │
│  .Persistence      │  .DatFile                     │
│  [net10.0]         │  [net10.0-windows, x86]       │
│  EF Core, MSSQL    │  P/Invoke, datexport.dll      │
│  (Web App)         │  (CLI only)                   │
└────────────────────┴───────────────────────────────┘

┌────────────────────────────────────────────────────┐
│  CLI  [net10.0-windows, x86]                       │
│  Osobny host, ten sam Application layer            │
│  export/patch via MediatR                          │
└────────────────────────────────────────────────────┘
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
przejsc na per-project. Infrastructure trzeba rozszczepic na .DatFile i .Persistence,
inaczej TFM mismatch blokuje Web App.

### Kluczowe decyzje

| Decyzja | Wybor | Dlaczego |
|---------|-------|----------|
| DB | **MSSQL** (EF Core, code-first) | Znany stack, Docker lub LocalDB |
| Frontend | **Blazor SSR** | C# stack, shared models, SSR + interactive |
| Auth | **Brak (deferred)** | Single-user, localhost |
| API layer | **Brak** | Blazor -> MediatR bezposrednio (in-process) |
| Multi-language | **W strukturach** | Schema ma LanguageCode, UI only Polish |
| Docker | **Rekomendowany** | docker-compose: MSSQL + Web App |

### Model tlumaczen

Single-user. Tlumaczenia edytowane bezposrednio w lokalnym UI.

- Multi-language schema (Languages table, LanguageCode FK) — future-proofing
- Aktywny development: tylko Polish
- Approved field w schema, ale domyslnie wszystko approved (single-user)
- Glossary per jezyk — spojna terminologia
- Export generuje {lang}.txt do uzycia z CLI patch

### Workflow

```
1. CLI export  ->  DAT -> exported.txt (angielskie teksty)
2. Web App import  ->  exported.txt -> MSSQL (English reference)
3. Web App: edytuje tlumaczenia side-by-side EN/PL
4. Web App export  ->  DB -> polish.txt
5. CLI patch  ->  polish.txt -> DAT
6. Odpal gre
```

Opcjonalnie BAT: `export.bat` -> import w przegladarce -> tlumacz -> export -> `patch.bat && run.bat`

---

## M1: MediatR Clean Architecture Refactor

**Cel:** CLI jako cienki dispatcher — `IMediator.Send()`. Fundament pod web app.

**Decyzje:**
- Handlers **zastepuja** serwisy (Exporter, Patcher). `IExporter`/`IPatcher` -> usuniete.
- Progress via `IProgress<T>` w DI — nie `Action<int,int>` callback.
- PreflightCheckQuery zwraca `PreflightReport` (dane) — zero `Console.ReadLine()`.

**Struktura po M1:**
```
Application/
  Features/
    Export/
      ExportTextsQuery.cs              : IRequest<Result<ExportSummary>>
      ExportTextsQueryHandler.cs       : IRequestHandler  (zastepuje Exporter)
    Patch/
      ApplyPatchCommand.cs             : IRequest<Result<PatchSummary>>
      ApplyPatchCommandHandler.cs      : IRequestHandler  (zastepuje Patcher)
      PreflightCheckQuery.cs           : IRequest<Result<PreflightReport>>
      PreflightCheckQueryHandler.cs    : IRequestHandler  (dane, zero UI)
  Behaviors/
    LoggingPipelineBehavior.cs         : IPipelineBehavior
    ValidationPipelineBehavior.cs      : IPipelineBehavior

Usuniete:
  IExporter, IPatcher, Exporter, Patcher,
  PreflightChecker, ExportCommand, PatchCommand (static classes)
```

| #  | Issue | Priority | Depends On |
|----|-------|----------|------------|
| 1  | Restructure TFMs: split Infrastructure, per-project TFM/Platform | **CRITICAL** | — |
| 2  | Add MediatR NuGet packages | High | — |
| 3  | Design `IProgress<T>` pattern for handlers | High | — |
| 4  | ExportTextsQuery + Handler (zastepuje Exporter) | High | #2, #3 |
| 5  | ApplyPatchCommand + Handler (zastepuje Patcher) | High | #2, #3 |
| 6  | PreflightCheckQuery + Handler (dane, zero Console I/O) | Medium | #2 |
| 7  | LoggingPipelineBehavior | Medium | #2 |
| 8  | ValidationPipelineBehavior | Medium | #2 |
| 9  | Refactor CLI Program.cs -> IMediator dispatch | High | #4, #5 |
| 10 | Delete IExporter, IPatcher, Exporter, Patcher, static Commands | High | #9 |
| 11 | Update DI registration (AddMediatR, behaviors) | High | #2 |
| 12 | Fix args reordering: wire ArgsOrder/ArgsId in patch pipeline (or remove) | Medium | — |
| 13 | Implement approved field: model, parser reads 6th field, patcher filters | Medium | — |
| 14 | Unit tests for MediatR handlers | High | #4, #5 |
| 15 | Integration tests for MediatR pipeline | Medium | #14 |

---

## M2: Database Layer (MSSQL, local)

**Cel:** Flat file -> MSSQL (code-first EF Core). Multi-language schema (future-proofing).
CRUD, search, historia edycji, glossary. Koniec z pipe-separated plikami.

**MSSQL:**
- Docker: `mcr.microsoft.com/mssql/server:2022-latest` lub LocalDB
- EF Core provider: `Microsoft.EntityFrameworkCore.SqlServer`
- Connection string w `appsettings.json` / `appsettings.Development.json`
- Migrations: `dotnet ef` CLI, auto-migrate on startup (development)

**Dwa modele "Translation":**
- `Domain.Models.Translation` — init-only DTO dla DAT pipeline (FileId, GossipId, Content, `int[]?` ArgsOrder)
- `Persistence.Entities.TranslationEntity` — DB entity (Id, LanguageCode, timestamps, `string` ArgsOrder)
- Mapping w repository

**Schema (code-first EF Core -> MSSQL):**
```sql
CREATE TABLE Languages (
    Code            NVARCHAR(10) PRIMARY KEY,      -- 'pl', 'de', 'fr'
    Name            NVARCHAR(100) NOT NULL,         -- 'Polish'
    IsActive        BIT NOT NULL DEFAULT 0
);

CREATE TABLE ExportedTexts (
    Id              INT IDENTITY(1,1) PRIMARY KEY,
    FileId          INT NOT NULL,
    GossipId        INT NOT NULL,
    EnglishContent  NVARCHAR(MAX) NOT NULL,
    Tag             NVARCHAR(200),                  -- reczne tagowanie (nullable)
    ImportedAt      DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    CONSTRAINT UQ_ExportedTexts UNIQUE (FileId, GossipId)
);

CREATE TABLE Translations (
    Id              INT IDENTITY(1,1) PRIMARY KEY,
    FileId          INT NOT NULL,
    GossipId        INT NOT NULL,
    LanguageCode    NVARCHAR(10) NOT NULL,
    Content         NVARCHAR(MAX) NOT NULL,
    ArgsOrder       NVARCHAR(50),                   -- "1-2-3", NULL = default
    ArgsId          NVARCHAR(50),                   -- "1-2", NULL = default
    IsApproved      BIT NOT NULL DEFAULT 1,          -- single-user: default approved
    Notes           NVARCHAR(MAX),
    CreatedAt       DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    UpdatedAt       DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    CONSTRAINT UQ_Translations UNIQUE (FileId, GossipId, LanguageCode),
    CONSTRAINT FK_Translations_Language FOREIGN KEY (LanguageCode) REFERENCES Languages(Code)
);

CREATE TABLE TranslationHistory (
    Id              INT IDENTITY(1,1) PRIMARY KEY,
    TranslationId   INT NOT NULL,
    OldContent      NVARCHAR(MAX),
    NewContent      NVARCHAR(MAX) NOT NULL,
    ChangedAt       DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    CONSTRAINT FK_History_Translation FOREIGN KEY (TranslationId) REFERENCES Translations(Id)
);

CREATE TABLE Glossary (
    Id              INT IDENTITY(1,1) PRIMARY KEY,
    LanguageCode    NVARCHAR(10) NOT NULL,
    EnglishTerm     NVARCHAR(500) NOT NULL,
    TranslatedTerm  NVARCHAR(500) NOT NULL,
    Context         NVARCHAR(500),                  -- 'race name', 'location'
    CONSTRAINT UQ_Glossary UNIQUE (LanguageCode, EnglishTerm),
    CONSTRAINT FK_Glossary_Language FOREIGN KEY (LanguageCode) REFERENCES Languages(Code)
);
```

Brak tabel Users/Roles — dodane w przyszlosci gdy auth bedzie potrzebny.
Pola user FK (SubmittedById, ApprovedById, ChangedById) celowo pominiete —
dodane jako nullable kolumny w migracji gdy auth wejdzie.

| #  | Issue | Priority | Depends On |
|----|-------|----------|------------|
| 16 | Add EF Core + SQL Server NuGet packages | High | #1 |
| 17 | Schema design + TranslationEntity vs Translation DTO mapping | High | — |
| 18 | AppDbContext + entity configurations (Infrastructure.Persistence) | High | #16, #17 |
| 19 | ITranslationRepository abstraction (Application) | High | M1 |
| 20 | IExportedTextRepository abstraction (Application) | High | M1 |
| 21 | IGlossaryRepository abstraction (Application) | High | M1 |
| 22 | Implement repositories (Infrastructure.Persistence) | High | #18-#21 |
| 23 | EF migrations infrastructure + auto-migrate on startup (dev) | Medium | #18 |
| 24 | ImportExportedTextsCommand + Handler (exported.txt -> DB) | High | #20, #22 |
| 25 | Translation CRUD Commands/Queries (MediatR, language-aware) | High | #19, #22 |
| 26 | ExportTranslationsQuery + Handler (DB -> {lang}.txt) | High | #19, #22 |
| 27 | Glossary CRUD Commands/Queries | Medium | #21, #22 |
| 28 | Data migration: polish.txt -> DB (one-time, approved=1, lang='pl') | Medium | #22, #25 |
| 29 | Exported text parser (parse export.txt, group by FileId) | High | #24 |
| 30 | Handle `\|\|` separator in content (escape/unescape) | Medium | #25 |
| 31 | Seed: Polish language on first run | Medium | #18 |
| 32 | Unit tests for repositories and handlers | High | #22, #25 |

---

## M3: Local Web App (Blazor SSR)

**Cel:** Lokalna aplikacja webowa do tlumaczen. `dotnet run` -> przegladarka -> tlumacze.

**Tech:** Blazor SSR, no auth, MediatR in-process. Bootstrap dla stylu.

**Widoki:**
1. **Translation List** — tabela, search/filter/sort, pagination, status coloring
   (przetlumaczone / brak tlumaczenia / wersja robocza)
2. **Translation Editor** — side-by-side EN/PL, placeholders jako kolorowe tagi,
   glossary hints (podswietl termin jesli nie zgadza sie z glossary)
3. **File Browser** — group by FileId, full-text search po tresci
4. **Glossary Editor** — CRUD terminow
5. **Dashboard** — progress bars (przetlumaczone vs total), recent edits
6. **Import** — upload exported.txt -> DB
7. **Export** — download polish.txt

| #  | Issue | Priority | Depends On |
|----|-------|----------|------------|
| 33 | Create Blazor SSR project (net10.0), solution integration | High | M2 |
| 34 | Layout, navigation, basic styling (Bootstrap) | High | #33 |
| 35 | DI setup: MediatR, EF Core (SQL Server), DbContext, repositories | High | #33 |
| 36 | Translation List page (table, search, filter, sort, pagination) | High | #35 |
| 37 | Translation Editor (side-by-side EN/PL, save) | High | #35 |
| 38 | Syntax highlighting `<--DO_NOT_TOUCH!-->` + `\|\|` validation | Medium | #37 |
| 39 | Glossary hints in editor (highlight mismatched terms) | Medium | #37, #27 |
| 40 | File Browser (group by FileId, search) | Medium | #35 |
| 41 | Glossary Editor (CRUD) | Medium | #35 |
| 42 | Dashboard (progress stats, recent edits) | Medium | #35 |
| 43 | Import page (upload exported.txt -> DB) | High | #35, #24 |
| 44 | Export page (generate + download polish.txt) | High | #35, #26 |
| 45 | Keyboard shortcuts (Ctrl+S save, Ctrl+Enter save+next) | Low | #37 |
| 46 | Bulk operations (mark multiple as approved/draft) | Low | #36 |
| 47 | Error handling (Result -> user-friendly toast/messages) | Medium | #33 |
| 48 | BAT scripts: export, patch, run, full-workflow | Medium | M1 |
| 49 | Docker support (Dockerfile, docker-compose: Web App + MSSQL) | Medium | #33 |
| 50 | Setup/first-run: auto-migrate DB, seed Polish language | Medium | #33, #31 |
| 51 | Unit + integration tests for web components | High | #36, #37 |

---

## Execution Order

```
Sprint 1 (M1):  #1-#15   TFM split + MediatR refactor
Sprint 2 (M2):  #16-#32  MSSQL database layer
Sprint 3 (M3):  #33-#51  Local Blazor web app + workflow
```

Po M1 -> CLI dziala identycznie (MediatR pod spodem)
Po M2 -> baza gotowa, testy przechodza, import/export via handlers
Po M3 -> **moge tlumczyc w przegladarce**

**51 issues total.**

---

## Future (gdy pojawi sie community)

Nie w tym planie. Dodane gdy bedzie potrzeba.

| Feature | Opis |
|---------|------|
| Auth (OpenIddict) | Rejestracja, login, JWT, role per jezyk |
| Web API | REST API layer miedzy Blazor a Application (remote access) |
| Multi-user | SubmittedById, ApprovedById, review workflow |
| Multi-language UI | Language selector, inne jezyki aktywne |
| AI review | LLM sprawdza placeholders, grammar, terminologie |
| OAuth providers | GitHub, Discord login |
| Notifications | Discord webhook, email na nowe submissions |

---

## Podjete decyzje

| Decyzja | Wybor | Alternatywa (future) |
|---------|-------|----------------------|
| Baza danych | **MSSQL** (code-first EF Core) | — |
| Frontend | **Blazor SSR** | — |
| Auth | **Brak** (single-user, localhost) | OpenIddict |
| API layer | **Brak** (Blazor -> MediatR in-process) | ASP.NET Core Web API |
| Multi-language | **Schema only** (Polish active) | Full multi-language UI |
| Hosting | **localhost** (Docker compose / LocalDB) | Azure / VPS |

## Scope: later (nie w tym planie)

- AI review pipeline (LLM sprawdza placeholders, grammar, terminologie)
- Full review workflow (reviewer rola, states: submitted -> in_review -> approved)
- OAuth providers (GitHub, Discord) — na razie brak auth
- Notifications (email/Discord webhook na nowe submissions)
- Multi-user collaboration
- Remote deployment
