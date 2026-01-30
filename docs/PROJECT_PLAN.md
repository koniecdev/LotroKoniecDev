# LOTRO Polish Patcher - Project Plan

> From MVP to Product. Plan rozpisany jak PM — milestones, epics, issues, zależności.

## Current State (MVP - Done)

- CLI export/patch działa
- Tłumaczenia w grze śmigają
- Launcher z update checking
- ~550 test assertions (unit + integration)
- Clean Architecture (5 warstw), Result monad, P/Invoke

## Target State

```
                    ┌──────────────┐
                    │  Web App UI  │  (Blazor/React - translation editor)
                    │  net10.0     │
                    └──────┬───────┘
                           │ HTTP
   ┌───────────────────────┼───────────────────────────────┐
   │                       │                               │
   │  ┌────────────────────▼──┐       ┌────────────────┐   │
   │  │   Web API             │       │  CLI (dotnet)  │   │
   │  │   ASP.NET Core        │       │  net10.0-win   │   │
   │  │   net10.0             │       │  x86           │   │
   │  └────────────┬──────────┘       └───────┬────────┘   │
   │               │                          │            │
   │  ┌────────────▼──────────────────────────▼────────┐   │
   │  │         IMediator (MediatR)                    │   │
   │  │      Commands / Queries / Handlers             │   │
   │  ├────────────────────────────────────────────────┤   │
   │  │     LotroKoniecDev.Application  [net10.0]      │   │
   │  │   Features: Export, Patch, CRUD                │   │
   │  │   Behaviors: Logging, Validation               │   │
   │  ├────────────────────────────────────────────────┤   │
   │  │       LotroKoniecDev.Domain     [net10.0]      │   │
   │  │   Models, Result<T>, Errors, VOs               │   │
   │  ├───────────────────┬────────────────────────────┤   │
   │  │  Infrastructure   │  Infrastructure            │   │
   │  │  .Persistence     │  .DatFile                  │   │
   │  │  [net10.0]        │  [net10.0-windows, x86]    │   │
   │  │  EF Core, SQLite  │  P/Invoke, datexport.dll   │   │
   │  │  (Web API + CLI)  │  (CLI only)                │   │
   │  └───────────────────┴────────────────────────────┘   │
   └───────────────────────────────────────────────────────┘
```

**CRITICAL: TFM Split Required**

Current `Directory.Build.props` sets `net10.0-windows` + `x86` globally.
This MUST be changed before M2/M3:
- Domain, Application, Primitives → `net10.0` (no Windows APIs used)
- Infrastructure.Persistence → `net10.0` (EF Core, cross-platform)
- Infrastructure.DatFile → `net10.0-windows` + x86 (P/Invoke to datexport.dll)
- CLI → `net10.0-windows` + x86
- WebApi → `net10.0`
- WebApp (Blazor WASM) → `net10.0`

### Workflow (docelowy)

```
1. CLI export → DAT → exported.txt (angielskie teksty)
2. Web App import → exported.txt → baza danych (English reference)
3. Tłumacz pracuje w Web App (CRUD, full-text search EN/PL, side-by-side editor)
4. GET /api/translations/export → polish.txt (lub CLI sync)
5. CLI patch → polish.txt → DAT
6. Odpal grę — tłumaczenia w grze
```

Lub BAT one-liner: `export.bat && patch.bat && run.bat`

---

## Milestones

### M1: MediatR Clean Architecture Refactor

**Cel:** Refaktor CLI na CQRS z MediatR. Warstwa prezentacji (CLI) staje się cienkim
dispatcherem — identycznie jak Web API potem. Fundament pod resztę.

**Key design decisions (M1):**
- Handlers REPLACE existing service classes (Exporter, Patcher) — not wrap them.
  `IExporter`, `IPatcher` interfaces are deleted. Handlers ARE the use cases.
- PreflightCheckQuery returns `PreflightReport` data — NO Console.ReadLine()
  inside handler. CLI reads report and handles user interaction itself.
- Progress reporting via `IProgress<T>` injected via DI, not `Action<int,int>`
  callbacks in request objects.

**Architektura po M1:**
```
CLI Program.cs
  └─ IMediator.Send(new ExportTextsQuery { ... })
  └─ IMediator.Send(new ApplyPatchCommand { ... })

Application/
  Features/
    Export/
      ExportTextsQuery.cs              : IRequest<Result<ExportSummary>>
      ExportTextsQueryHandler.cs       : IRequestHandler<...>  (replaces Exporter)
    Patch/
      ApplyPatchCommand.cs             : IRequest<Result<PatchSummary>>
      ApplyPatchCommandHandler.cs      : IRequestHandler<...>  (replaces Patcher)
      PreflightCheckQuery.cs           : IRequest<Result<PreflightReport>>
      PreflightCheckQueryHandler.cs    : IRequestHandler<...>  (returns data, no UI)
  Behaviors/
    LoggingPipelineBehavior.cs         : IPipelineBehavior<,>
    ValidationPipelineBehavior.cs      : IPipelineBehavior<,>

DELETED after M1:
  - IExporter interface + Exporter class
  - IPatcher interface + Patcher class
  - PreflightChecker static class
  - ExportCommand / PatchCommand static classes
```

### M2: Translation Database Layer

**Cel:** Flat file → SQLite. Umożliwia CRUD, search, metadane, historię edycji.

**NOTE:** Domain model `Translation` (init-only DTO for DAT pipeline) is DIFFERENT
from DB entity `TranslationEntity`. Two separate models, mapped in repositories.

**Schema:**
```sql
-- Angielskie teksty z export DAT (reference)
CREATE TABLE ExportedTexts (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    FileId          INTEGER NOT NULL,
    GossipId        INTEGER NOT NULL,
    EnglishContent  TEXT NOT NULL,
    Tag             TEXT,           -- manually assigned by translator (nullable)
    ImportedAt      DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE(FileId, GossipId)
);
-- NOTE: No QuestTitle column. Export data has no quest metadata.
-- Full-text search on EnglishContent is the primary discovery mechanism.
-- Tag is optional manual categorization by translators.

-- Polskie tłumaczenia (CRUD)
CREATE TABLE Translations (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    FileId          INTEGER NOT NULL,
    GossipId        INTEGER NOT NULL,
    PolishContent   TEXT NOT NULL,
    ArgsOrder       TEXT,           -- "1-2-3" format, NULL if default
    ArgsId          TEXT,           -- "1-2" format, NULL if default
    IsApproved      INTEGER NOT NULL DEFAULT 0,
    CreatedAt       DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UpdatedAt       DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    Notes           TEXT,           -- translator comments
    UNIQUE(FileId, GossipId),
    FOREIGN KEY (FileId, GossipId) REFERENCES ExportedTexts(FileId, GossipId)
);

-- Historia zmian (audit trail)
CREATE TABLE TranslationHistory (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    TranslationId   INTEGER NOT NULL,
    OldContent      TEXT,
    NewContent      TEXT NOT NULL,
    ChangedAt       DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (TranslationId) REFERENCES Translations(Id)
);
```

### M3: Web API

**Cel:** ASP.NET Core Web API — drugi presentation layer obok CLI. Oba korzystają
z tego samego Application layer via MediatR.

**Endpoints:**
```
POST   /api/import/exported-texts       Import exported.txt → DB
POST   /api/import/translations         Import polish.txt → DB (migracja)

GET    /api/translations                List (paginated, filterable)
GET    /api/translations/{id}           Single translation
POST   /api/translations                Create
PUT    /api/translations/{id}           Update
DELETE /api/translations/{id}           Delete
PATCH  /api/translations/{id}/approve   Toggle approval

GET    /api/translations/search?q=      Full-text search (EN/PL content, FileId groups)
GET    /api/translations/stats          Progress dashboard data

GET    /api/export/polish.txt           Download polish.txt (approved translations)
GET    /api/exported-texts              List English texts (reference)
GET    /api/exported-texts/search?q=    Search English texts
```

### M4: Translation Web App (Frontend)

**Cel:** User-friendly UI do tłumaczeń. Koniec z ręczną edycją pipe-separated plików.

**Widoki:**
1. **Translation List** — tabela z search/filter/sort, pagination
2. **Translation Editor** — side-by-side EN/PL, syntax highlighting `<--DO_NOT_TOUCH!-->`,
   args order editor, approve checkbox, notes field
3. **File Browser** — group by FileId, full-text search EN/PL, list all fragments in file
   (NOTE: no quest title metadata available — search is by content, not quest names)
4. **Dashboard** — progress (translated/total), approved/pending, last edits
5. **Import/Export** — upload exported.txt, download polish.txt

**Tech decision:** Blazor WASM (trzymamy C# stack) lub React (lepszy ecosystem UI).
Rekomendacja: **Blazor WASM** — jednolity stack, shared models z Domain, bez osobnego
build pipeline. Alternatywnie React + Vite jeśli preferujesz.

### M5: Workflow Automation

**Cel:** BAT files, CLI ↔ API integration, end-to-end flow.

**BAT files:**
```bat
:: export.bat — Export English texts from DAT
LotroKoniecDev.exe export

:: import.bat — Import exported texts to web app DB
curl -X POST http://localhost:5000/api/import/exported-texts -F "file=@data/exported.txt"

:: patch.bat — Download translations & patch DAT
curl -o translations/polish.txt http://localhost:5000/api/export/polish.txt
LotroKoniecDev.exe patch polish

:: run.bat — Launch LOTRO
start "" "C:\Program Files\LOTRO\LotroLauncher.exe"

:: full-workflow.bat — Everything in one go
call export.bat && call import.bat && call patch.bat && call run.bat
```

---

## Issues Breakdown

### M1: MediatR Clean Architecture Refactor

| #  | Issue | Priority | Depends On |
|----|-------|----------|------------|
| 1  | Restructure TFMs: split Infrastructure, remove global net10.0-windows | **CRITICAL** | — |
| 2  | Add MediatR NuGet packages to solution | High | — |
| 3  | Design progress reporting pattern (IProgress\<T> via DI) | High | — |
| 4  | Create ExportTextsQuery + Handler (replaces Exporter) | High | #2, #3 |
| 5  | Create ApplyPatchCommand + Handler (replaces Patcher) | High | #2, #3 |
| 6  | Create PreflightCheckQuery + Handler (returns data, no Console I/O) | Medium | #2 |
| 7  | Add LoggingPipelineBehavior | Medium | #2 |
| 8  | Add ValidationPipelineBehavior | Medium | #2 |
| 9  | Refactor CLI Program.cs to use IMediator dispatch | High | #4, #5 |
| 10 | Delete IExporter, IPatcher, Exporter, Patcher, static Commands | High | #9 |
| 11 | Update DI registration (AddMediatR, Behaviors) | High | #2 |
| 12 | Fix or remove dead code: args reordering (Patcher ignores ArgsOrder) | Medium | — |
| 13 | Implement approved field: add to Translation model, parser, filtering | Medium | — |
| 14 | Update unit tests for MediatR handlers | High | #4, #5 |
| 15 | Update integration tests for MediatR pipeline | Medium | #14 |

### M2: Translation Database Layer

| #  | Issue | Priority | Depends On |
|----|-------|----------|------------|
| 16 | Add EF Core + SQLite NuGet packages | High | #1 (TFM split) |
| 17 | Design database schema + define TranslationEntity vs Translation DTO | High | — |
| 18 | Create AppDbContext + entity configurations (in Infrastructure.Persistence) | High | #16, #17 |
| 19 | Create ITranslationRepository abstraction (Application layer) | High | M1 |
| 20 | Create IExportedTextRepository abstraction (Application layer) | High | M1 |
| 21 | Implement repositories in Infrastructure.Persistence | High | #18, #19, #20 |
| 22 | Create EF migrations infrastructure | Medium | #18 |
| 23 | Create ImportExportedTextsCommand + Handler | High | #20, #21 |
| 24 | Create CRUD Commands/Queries for Translations | High | #19, #21 |
| 25 | Create ExportTranslationsQuery + Handler (DB → polish.txt format) | High | #19, #21 |
| 26 | Create data migration tool: polish.txt → DB (one-time, set approved=1) | Medium | #21, #24 |
| 27 | Add exported text parser (parse export.txt format, group by FileId) | High | #23 |
| 28 | Handle \|\| separator in translation content (escape/unescape) | Medium | #24 |
| 29 | Unit tests for repositories and handlers | High | #21, #24 |

### M3: Web API

| #  | Issue | Priority | Depends On |
|----|-------|----------|------------|
| 30 | Create LotroKoniecDev.WebApi project (net10.0, cross-platform) | High | #1, M2 |
| 31 | Configure DI, middleware, CORS in WebApi | High | #30 |
| 32 | Create TranslationsController (CRUD endpoints) | High | #30, #24 |
| 33 | Create ImportController (exported-texts, translations import) | High | #30, #23 |
| 34 | Create ExportController (GET polish.txt download) | High | #30, #25 |
| 35 | Create SearchController (full-text search EN/PL texts) | Medium | #30 |
| 36 | Add pagination, filtering, sorting to list endpoints | Medium | #32 |
| 37 | Add Swagger/OpenAPI documentation | Low | #30 |
| 38 | Add StatsController (progress dashboard data) | Medium | #30 |
| 39 | Integration tests for API endpoints | High | #32, #33, #34 |
| 40 | Add error handling middleware (Result → HTTP status mapping) | Medium | #30 |

### M4: Translation Web App (Frontend)

| #  | Issue | Priority | Depends On |
|----|-------|----------|------------|
| 41 | Create frontend project (Blazor WASM or React, net10.0) | High | M3 |
| 42 | Create API client / HTTP service layer | High | #41 |
| 43 | Create Translation List view (table + search + filter + sort) | High | #42 |
| 44 | Create Translation Editor (side-by-side EN/PL, args editor) | High | #42 |
| 45 | Create File Browser (group by FileId, full-text search, NOT quest title) | Medium | #42, #35 |
| 46 | Create Dashboard view (progress stats, recent edits) | Medium | #42, #38 |
| 47 | Create Import/Export page (upload/download files) | Medium | #42 |
| 48 | Add syntax highlighting for `<--DO_NOT_TOUCH!-->` + validate \|\| in input | Medium | #44 |
| 49 | Add keyboard shortcuts for efficient translation workflow | Low | #44 |
| 50 | Add bulk operations (approve/reject multiple) | Low | #43 |
| 51 | Responsive design / mobile support | Low | #43 |

### M5: Workflow Automation

| #  | Issue | Priority | Depends On |
|----|-------|----------|------------|
| 52 | Add CLI command: `sync` (fetch polish.txt from API) | Medium | M3 |
| 53 | Create BAT files: export, import, patch, run, full-workflow | Medium | #52 |
| 54 | Add CLI command: `import` (push exported.txt to API) | Medium | M3 |
| 55 | Update README with new workflow documentation | Low | M4 |
| 56 | Docker Compose for Web API + DB ONLY (CLI stays native Windows) | Low | M3 |
| 57 | Create setup/first-run script (DB migration, initial import) | Medium | M2 |

---

## Recommended Execution Order

```
Sprint 1 (M1):  Issues #1-#15  — TFM split + MediatR refactor + dead code cleanup
Sprint 2 (M2):  Issues #16-#29 — Database layer
Sprint 3 (M3):  Issues #30-#40 — Web API
Sprint 4 (M4):  Issues #41-#51 — Frontend
Sprint 5 (M5):  Issues #52-#57 — Automation & polish
```

**FIRST THING in Sprint 1:** Issue #1 (TFM restructuring). Everything else blocked by it.

Each milestone is independently deployable. After M1 the CLI still works identically.
After M2+M3 you can already use API for translations without frontend (Postman/curl).
M4 adds the UI. M5 ties everything together.

**Total: 57 issues** (was 52, added 5 from self-review)

## Known Pre-existing Issues Found During Review

1. **Args reordering never applied** — `Patcher` sets `fragment.Pieces` but never calls
   `SubFile.Serialize(argsOrder, argsId, fragmentId)`. `SubFile.ReorderArguments()` exists
   but is dead code. Issue #12 tracks this.
2. **`approved` field is dead** — Exporter writes `||1`, Parser ignores 6th field,
   `Translation` model has no `IsApproved`. Issue #13 tracks this.
3. **`||` in content breaks parser** — `TranslationFileParser` splits by `||` without
   escaping. A translation containing literal `||` produces wrong field count. Issue #28.

## Risk Notes

- **SQLite vs PostgreSQL**: SQLite is simplest for single-user local use. If multi-user
  or remote deployment needed → PostgreSQL. Decision at M2 start.
- **Blazor WASM vs React**: Blazor keeps C# stack unified and can share Domain models
  (since Domain will target `net10.0`). React has better ecosystem for complex UIs.
  Decision at issue #41.
- **Docker**: Only for Web API + DB. CLI requires Windows x86 + native DLLs.
  `full-workflow.bat` runs on host, not in container.

## Self-Review

See [PLAN_SELF_REVIEW.md](./PLAN_SELF_REVIEW.md) for full analysis of 4 critical issues,
4 important clarifications, and 6 minor findings that were corrected in this plan.
