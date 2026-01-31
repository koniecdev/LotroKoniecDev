# LOTRO Polish Patcher - Project Plan

> Od MVP do produktu. 5 milestones, 57 issues, pełna architektura.

## Stan obecny (MVP)

- CLI export/patch działa, tłumaczenia w grze śmigają
- Launcher z update checking
- ~550 test assertions (unit + integration)
- Clean Architecture (5 warstw), Result monad, P/Invoke
- Brak MediatR — komendy to statyczne klasy
- Tłumaczenia w pliku pipe-delimited — edycja karkołomna
- Pola `approved` i `args_order` w formacie pliku, ale parser/patcher je ignorują (dead code)

## Architektura docelowa

```
                ┌──────────────┐
                │  Web App UI  │  Blazor WASM / React
                │  net10.0     │  translation editor
                └──────┬───────┘
                       │ HTTP
 ┌─────────────────────┼───────────────────────────────┐
 │                     │                               │
 │  ┌──────────────────▼──┐       ┌────────────────┐   │
 │  │   Web API           │       │  CLI           │   │
 │  │   ASP.NET Core      │       │  net10.0-win   │   │
 │  │   net10.0           │       │  x86           │   │
 │  └──────────┬──────────┘       └───────┬────────┘   │
 │             │                          │            │
 │  ┌──────────▼──────────────────────────▼────────┐   │
 │  │         IMediator (MediatR)                  │   │
 │  │      Commands / Queries / Handlers           │   │
 │  ├──────────────────────────────────────────────┤   │
 │  │     LotroKoniecDev.Application  [net10.0]    │   │
 │  │   Handlers: Export, Patch, Translation CRUD  │   │
 │  │   Behaviors: Logging, Validation             │   │
 │  ├──────────────────────────────────────────────┤   │
 │  │       LotroKoniecDev.Domain     [net10.0]    │   │
 │  │   Models, Result<T>, Errors, VOs             │   │
 │  ├───────────────────┬──────────────────────────┤   │
 │  │  Infrastructure   │  Infrastructure          │   │
 │  │  .Persistence     │  .DatFile                │   │
 │  │  [net10.0]        │  [net10.0-windows, x86]  │   │
 │  │  EF Core, SQLite  │  P/Invoke, datexport.dll │   │
 │  │  (Web API + CLI)  │  (CLI only)              │   │
 │  └───────────────────┴──────────────────────────┘   │
 └─────────────────────────────────────────────────────┘
```

### TFM — per project, nie globalny

Obecny `Directory.Build.props` ustawia `net10.0-windows` + `x86` globalnie.
Web API i Blazor WASM tego nie zbudują. Wymagany split:

| Projekt | TFM | Platform |
|---------|-----|----------|
| Primitives, Domain, Application | `net10.0` | AnyCPU |
| Infrastructure.Persistence | `net10.0` | AnyCPU |
| Infrastructure.DatFile | `net10.0-windows` | x86 |
| CLI | `net10.0-windows` | x86 |
| WebApi | `net10.0` | AnyCPU |
| WebApp (Blazor WASM) | `net10.0` | AnyCPU |

Infrastructure musi się rozszczepić na dwa projekty — .DatFile (P/Invoke,
native DLLs, referowany tylko z CLI) i .Persistence (EF Core, referowany
z CLI i Web API). Inaczej TFM mismatch blokuje kompilację.

### Workflow

```
1. CLI export  →  DAT → exported.txt (angielskie teksty)
2. Web App import  →  exported.txt → baza danych (English reference)
3. Tłumacz pracuje w Web App (CRUD, full-text search EN/PL, side-by-side editor)
4. GET /api/translations/export  →  polish.txt
5. CLI patch  →  polish.txt → DAT
6. Odpal grę
```

BAT one-liner: `sync.bat && patch.bat && run.bat`

---

## M1: MediatR Clean Architecture Refactor

**Cel:** CLI staje się cienkim dispatcherem — `IMediator.Send()`. Identycznie
jak Web API potem. Fundament pod resztę.

**Decyzje:**
- Handlers **zastępują** istniejące serwisy (Exporter, Patcher). Handler IS use case.
  `IExporter`, `IPatcher` interfejsy + klasy → usunięte.
- Progress reporting via `IProgress<T>` injected DI, nie `Action<int,int>` callback.
  CLI rejestruje `ConsoleProgress`, Web API → `NoOpProgress` albo WebSocket.
- PreflightCheckQuery zwraca `PreflightReport` (dane) — zero `Console.ReadLine()`.
  CLI sam decyduje o promptach na podstawie raportu.

**Struktura po M1:**
```
Application/
  Features/
    Export/
      ExportTextsQuery.cs              : IRequest<Result<ExportSummary>>
      ExportTextsQueryHandler.cs       : IRequestHandler  (zastępuje Exporter)
    Patch/
      ApplyPatchCommand.cs             : IRequest<Result<PatchSummary>>
      ApplyPatchCommandHandler.cs      : IRequestHandler  (zastępuje Patcher)
      PreflightCheckQuery.cs           : IRequest<Result<PreflightReport>>
      PreflightCheckQueryHandler.cs    : IRequestHandler  (dane, zero UI)
  Behaviors/
    LoggingPipelineBehavior.cs         : IPipelineBehavior
    ValidationPipelineBehavior.cs      : IPipelineBehavior

Usunięte:
  IExporter, IPatcher, Exporter, Patcher,
  PreflightChecker, ExportCommand, PatchCommand (static classes)
```

**Issues:**

| #  | Issue | Priority | Depends On |
|----|-------|----------|------------|
| 1  | Restructure TFMs: split Infrastructure, per-project TFM/Platform | **CRITICAL** | — |
| 2  | Add MediatR NuGet packages to solution | High | — |
| 3  | Design progress reporting pattern (`IProgress<T>` via DI) | High | — |
| 4  | Create ExportTextsQuery + Handler (zastępuje Exporter) | High | #2, #3 |
| 5  | Create ApplyPatchCommand + Handler (zastępuje Patcher) | High | #2, #3 |
| 6  | Create PreflightCheckQuery + Handler (zwraca dane, zero Console I/O) | Medium | #2 |
| 7  | Add LoggingPipelineBehavior | Medium | #2 |
| 8  | Add ValidationPipelineBehavior | Medium | #2 |
| 9  | Refactor CLI Program.cs to use IMediator dispatch | High | #4, #5 |
| 10 | Delete IExporter, IPatcher, Exporter, Patcher, static Commands | High | #9 |
| 11 | Update DI registration (AddMediatR, Behaviors) | High | #2 |
| 12 | Fix args reordering: wire ArgsOrder/ArgsId in patch pipeline (or remove dead code) | Medium | — |
| 13 | Implement approved field: add to model, parser reads 6th field, patcher filters | Medium | — |
| 14 | Update unit tests for MediatR handlers | High | #4, #5 |
| 15 | Update integration tests for MediatR pipeline | Medium | #14 |

---

## M2: Translation Database Layer

**Cel:** Flat file → SQLite. CRUD, search, historia edycji. Koniec z ręczną
edycją pipe-separated plików.

**Dwa modele "Translation":**
- `Domain.Models.Translation` — init-only DTO dla DAT pipeline (FileId, GossipId, Content, ArgsOrder as `int[]?`)
- `Infrastructure.Persistence.Entities.TranslationEntity` — DB entity (Id, timestamps, Notes, ArgsOrder as string)
- Mapping w repository

**Schema:**
```sql
CREATE TABLE ExportedTexts (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    FileId          INTEGER NOT NULL,
    GossipId        INTEGER NOT NULL,
    EnglishContent  TEXT NOT NULL,
    Tag             TEXT,           -- ręczne tagowanie przez tłumacza (nullable)
    ImportedAt      DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE(FileId, GossipId)
);
-- Brak QuestTitle — export z DAT nie zawiera metadanych o questach.
-- Odkrywanie tekstów via full-text search na EnglishContent.

CREATE TABLE Translations (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    FileId          INTEGER NOT NULL,
    GossipId        INTEGER NOT NULL,
    PolishContent   TEXT NOT NULL,
    ArgsOrder       TEXT,           -- "1-2-3" format, NULL = default
    ArgsId          TEXT,           -- "1-2" format, NULL = default
    IsApproved      INTEGER NOT NULL DEFAULT 0,
    CreatedAt       DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UpdatedAt       DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    Notes           TEXT,
    UNIQUE(FileId, GossipId),
    FOREIGN KEY (FileId, GossipId) REFERENCES ExportedTexts(FileId, GossipId)
);

CREATE TABLE TranslationHistory (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    TranslationId   INTEGER NOT NULL,
    OldContent      TEXT,
    NewContent      TEXT NOT NULL,
    ChangedAt       DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (TranslationId) REFERENCES Translations(Id)
);
```

**Issues:**

| #  | Issue | Priority | Depends On |
|----|-------|----------|------------|
| 16 | Add EF Core + SQLite NuGet packages | High | #1 (TFM split) |
| 17 | Design database schema + TranslationEntity vs Translation DTO mapping | High | — |
| 18 | Create AppDbContext + entity configurations (Infrastructure.Persistence) | High | #16, #17 |
| 19 | Create ITranslationRepository abstraction (Application layer) | High | M1 |
| 20 | Create IExportedTextRepository abstraction (Application layer) | High | M1 |
| 21 | Implement repositories in Infrastructure.Persistence | High | #18, #19, #20 |
| 22 | Create EF migrations infrastructure | Medium | #18 |
| 23 | Create ImportExportedTextsCommand + Handler (exported.txt → DB) | High | #20, #21 |
| 24 | Create Translation CRUD Commands/Queries (MediatR) | High | #19, #21 |
| 25 | Create ExportTranslationsQuery + Handler (DB → polish.txt format) | High | #19, #21 |
| 26 | Data migration: polish.txt → DB (one-time, mark all approved=1) | Medium | #21, #24 |
| 27 | Exported text parser (parse export.txt format, group by FileId) | High | #23 |
| 28 | Handle `\|\|` separator in translation content (escape/unescape) | Medium | #24 |
| 29 | Unit tests for repositories and handlers | High | #21, #24 |

---

## M3: Web API

**Cel:** ASP.NET Core Web API (`net10.0`, cross-platform) — drugi presentation layer
obok CLI. Oba dispatachują przez ten sam `IMediator` do tych samych handlerów.

Web API robi tylko operacje bazodanowe — nie dotyka plików DAT.

**Endpoints:**
```
POST   /api/import/exported-texts       Upload exported.txt → DB
POST   /api/import/translations         Upload polish.txt → DB (migracja)

GET    /api/translations                List (paginated, filterable)
GET    /api/translations/{id}           Single
POST   /api/translations                Create
PUT    /api/translations/{id}           Update
DELETE /api/translations/{id}           Delete
PATCH  /api/translations/{id}/approve   Toggle approval

GET    /api/translations/search?q=      Full-text search EN/PL
GET    /api/translations/stats          Progress dashboard data

GET    /api/export/polish.txt           Download (approved translations)
GET    /api/exported-texts              List English texts
GET    /api/exported-texts/search?q=    Search English texts
```

**Issues:**

| #  | Issue | Priority | Depends On |
|----|-------|----------|------------|
| 30 | Create LotroKoniecDev.WebApi project (net10.0, cross-platform) | High | #1, M2 |
| 31 | Configure DI, middleware, CORS | High | #30 |
| 32 | TranslationsController (CRUD) | High | #30, #24 |
| 33 | ImportController (upload exported-texts, translations) | High | #30, #23 |
| 34 | ExportController (GET polish.txt download) | High | #30, #25 |
| 35 | SearchController (full-text search EN/PL) | Medium | #30 |
| 36 | Pagination, filtering, sorting on list endpoints | Medium | #32 |
| 37 | Swagger/OpenAPI documentation | Low | #30 |
| 38 | StatsController (progress dashboard data) | Medium | #30 |
| 39 | Integration tests for API endpoints | High | #32, #33, #34 |
| 40 | Error handling middleware (Result → HTTP status mapping) | Medium | #30 |

---

## M4: Translation Web App (Frontend)

**Cel:** User-friendly UI. Koniec z pipe-separated plikami.

**Tech:** Blazor WASM (jeden stack C#, shared Domain models) lub React + Vite
(lepszy ecosystem UI). Decyzja na starcie M4.

**Widoki:**

1. **Translation List** — tabela, search/filter/sort, pagination, status coloring
2. **Translation Editor** — side-by-side EN/PL, `<--DO_NOT_TOUCH!-->` jako kolorowe
   tagi, args order editor, approve, notes, Ctrl+S / Ctrl+Enter
3. **File Browser** — group by FileId, full-text search. Brak metadanych questowych
   w exporcie — szukanie po treści, nie po tytule questa.
4. **Dashboard** — progress bars (translated/total, approved/pending), recent edits
5. **Import/Export** — upload exported.txt, download polish.txt

**Issues:**

| #  | Issue | Priority | Depends On |
|----|-------|----------|------------|
| 41 | Create frontend project (Blazor WASM or React, net10.0) | High | M3 |
| 42 | API client / HTTP service layer | High | #41 |
| 43 | Translation List view | High | #42 |
| 44 | Translation Editor (side-by-side EN/PL) | High | #42 |
| 45 | File Browser (group by FileId, full-text search) | Medium | #42, #35 |
| 46 | Dashboard (progress stats, recent edits) | Medium | #42, #38 |
| 47 | Import/Export page | Medium | #42 |
| 48 | Syntax highlighting `<--DO_NOT_TOUCH!-->` + walidacja `\|\|` w input | Medium | #44 |
| 49 | Keyboard shortcuts (Ctrl+S, Ctrl+Enter, Alt+arrows) | Low | #44 |
| 50 | Bulk operations (approve/reject multiple) | Low | #43 |
| 51 | Responsive design / mobile | Low | #43 |

---

## M5: Workflow Automation

**Cel:** BAT files, CLI ↔ API sync, end-to-end flow jednym kliknięciem.

```bat
scripts/
  export.bat          CLI export (DAT → exported.txt)
  import.bat          Upload exported.txt do API
  sync.bat            Download polish.txt z API
  patch.bat           CLI patch (polish.txt → DAT)
  run.bat             Odpal LOTRO
  full-workflow.bat   sync → patch → run
  dev-start.bat       Start Web API + otwórz przeglądarkę
```

**Issues:**

| #  | Issue | Priority | Depends On |
|----|-------|----------|------------|
| 52 | CLI command: `sync` (fetch polish.txt from API) | Medium | M3 |
| 53 | BAT files: export, import, sync, patch, run, full-workflow | Medium | #52 |
| 54 | CLI command: `import` (push exported.txt to API) | Medium | M3 |
| 55 | Update README with workflow documentation | Low | M4 |
| 56 | Docker Compose for Web API + DB only (CLI stays native Windows) | Low | M3 |
| 57 | Setup/first-run script (DB migration, initial import) | Medium | M2 |

---

## Execution Order

```
Sprint 1 (M1):  #1-#15   TFM split + MediatR refactor + dead code cleanup
Sprint 2 (M2):  #16-#29  Database layer
Sprint 3 (M3):  #30-#40  Web API
Sprint 4 (M4):  #41-#51  Frontend
Sprint 5 (M5):  #52-#57  Automation
```

Issue #1 (TFM restructuring) jest FIRST THING — blokuje M2/M3/M4.

Każdy milestone jest niezależnie deployowalny. Po M1 CLI działa identycznie.
Po M2+M3 można korzystać z API bez frontendu (Postman/curl). M4 dodaje UI.
M5 spina wszystko w jedno.

**57 issues total.**

## Decyzje do podjęcia

| Kiedy | Decyzja | Opcje | Rekomendacja |
|-------|---------|-------|-------------|
| Start M2 | SQLite vs PostgreSQL | SQLite (local), Postgres (multi-user) | SQLite |
| Start M4 | Blazor WASM vs React | Blazor (C# stack), React (ecosystem) | Blazor |
| Issue #12 | Args reordering | Fix (wire do Patcher) vs remove dead code | Fix |
