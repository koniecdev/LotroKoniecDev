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
                    └──────┬───────┘
                           │ HTTP
                    ┌──────▼───────┐       ┌───────────────┐
                    │   Web API    │       │   CLI (dotnet) │
                    │  ASP.NET Core│       │   LotroKoniecDev
                    └──────┬───────┘       └───────┬───────┘
                           │                       │
                    ┌──────▼───────────────────────▼───────┐
                    │         IMediator (MediatR)          │
                    │      Commands / Queries / Handlers   │
                    ├──────────────────────────────────────┤
                    │     LotroKoniecDev.Application       │
                    │   Features: Export, Patch, CRUD      │
                    │   Behaviors: Logging, Validation     │
                    ├──────────────────────────────────────┤
                    │       LotroKoniecDev.Domain          │
                    │   Models, Result<T>, Errors, VOs     │
                    ├──────────────────────────────────────┤
                    │    LotroKoniecDev.Infrastructure     │
                    │  DAT I/O │ EF Core/SQLite │ Network  │
                    └──────────────────────────────────────┘
```

### Workflow (docelowy)

```
1. CLI export → DAT → exported.txt (angielskie teksty)
2. Web App import → exported.txt → baza danych (English reference)
3. Tłumacz pracuje w Web App (CRUD, search by quest, side-by-side EN/PL)
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

**Architektura po M1:**
```
CLI Program.cs
  └─ IMediator.Send(new ExportTextsQuery { ... })
  └─ IMediator.Send(new ApplyPatchCommand { ... })

Application/
  Features/
    Export/
      ExportTextsQuery.cs              : IRequest<Result<ExportSummary>>
      ExportTextsQueryHandler.cs       : IRequestHandler<...>
    Patch/
      ApplyPatchCommand.cs             : IRequest<Result<PatchSummary>>
      ApplyPatchCommandHandler.cs      : IRequestHandler<...>
      PreflightCheckQuery.cs           : IRequest<Result<PreflightReport>>
      PreflightCheckQueryHandler.cs    : IRequestHandler<...>
  Behaviors/
    LoggingPipelineBehavior.cs         : IPipelineBehavior<,>
    ValidationPipelineBehavior.cs      : IPipelineBehavior<,>
```

### M2: Translation Database Layer

**Cel:** Flat file → SQLite. Umożliwia CRUD, search, metadane, historię edycji.

**Schema:**
```sql
-- Angielskie teksty z export DAT (reference)
CREATE TABLE ExportedTexts (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    FileId          INTEGER NOT NULL,
    GossipId        INTEGER NOT NULL,
    EnglishContent  TEXT NOT NULL,
    QuestTitle      TEXT,           -- extracted from content heuristics
    Category        TEXT,           -- quest/item/npc/ui/etc
    ImportedAt      DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE(FileId, GossipId)
);

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

GET    /api/translations/search?q=      Full-text search (quest title, EN/PL content)
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
3. **Quest Browser** — search po quest title, pokaż wszystkie stringi questa
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
| 1  | Add MediatR NuGet packages to solution | High | — |
| 2  | Create ExportTextsQuery + Handler (CQRS) | High | #1 |
| 3  | Create ApplyPatchCommand + Handler (CQRS) | High | #1 |
| 4  | Create PreflightCheckQuery + Handler | Medium | #1 |
| 5  | Add LoggingPipelineBehavior | Medium | #1 |
| 6  | Add ValidationPipelineBehavior | Medium | #1 |
| 7  | Refactor CLI Program.cs to use IMediator dispatch | High | #2, #3 |
| 8  | Remove static Command classes, migrate to MediatR | High | #7 |
| 9  | Update DI registration (AddMediatR, Behaviors) | High | #1 |
| 10 | Update unit tests for MediatR handlers | High | #2, #3 |
| 11 | Update integration tests for MediatR pipeline | Medium | #10 |

### M2: Translation Database Layer

| #  | Issue | Priority | Depends On |
|----|-------|----------|------------|
| 12 | Add EF Core + SQLite NuGet packages | High | — |
| 13 | Design database schema (ExportedTexts, Translations, History) | High | — |
| 14 | Create AppDbContext + entity configurations | High | #12, #13 |
| 15 | Create ITranslationRepository abstraction (Application layer) | High | M1 |
| 16 | Create IExportedTextRepository abstraction (Application layer) | High | M1 |
| 17 | Implement repositories (Infrastructure layer) | High | #14, #15, #16 |
| 18 | Create EF migrations infrastructure | Medium | #14 |
| 19 | Create ImportExportedTextsCommand + Handler | High | #16, #17 |
| 20 | Create CRUD Commands/Queries for Translations | High | #15, #17 |
| 21 | Create ExportTranslationsQuery + Handler (DB → polish.txt format) | High | #15, #17 |
| 22 | Create data migration tool: polish.txt → DB (one-time import) | Medium | #17, #20 |
| 23 | Add exported text parser (export.txt → structured data) | High | #19 |
| 24 | Unit tests for repositories and handlers | High | #17, #20 |

### M3: Web API

| #  | Issue | Priority | Depends On |
|----|-------|----------|------------|
| 25 | Create LotroKoniecDev.WebApi project (ASP.NET Core) | High | M1, M2 |
| 26 | Configure DI, middleware, CORS in WebApi | High | #25 |
| 27 | Create TranslationsController (CRUD endpoints) | High | #25, #20 |
| 28 | Create ImportController (exported-texts, translations import) | High | #25, #19 |
| 29 | Create ExportController (GET polish.txt download) | High | #25, #21 |
| 30 | Create SearchController (full-text search EN/PL texts) | Medium | #25 |
| 31 | Add pagination, filtering, sorting to list endpoints | Medium | #27 |
| 32 | Add Swagger/OpenAPI documentation | Low | #25 |
| 33 | Add StatsController (progress dashboard data) | Medium | #25 |
| 34 | Integration tests for API endpoints | High | #27, #28, #29 |
| 35 | Add error handling middleware (Result → HTTP status mapping) | Medium | #25 |

### M4: Translation Web App (Frontend)

| #  | Issue | Priority | Depends On |
|----|-------|----------|------------|
| 36 | Create frontend project (Blazor WASM or React) | High | M3 |
| 37 | Create API client / HTTP service layer | High | #36 |
| 38 | Create Translation List view (table + search + filter + sort) | High | #37 |
| 39 | Create Translation Editor (side-by-side EN/PL, args editor) | High | #37 |
| 40 | Create Quest Browser view (search by quest title) | Medium | #37, #30 |
| 41 | Create Dashboard view (progress stats, recent edits) | Medium | #37, #33 |
| 42 | Create Import/Export page (upload/download files) | Medium | #37 |
| 43 | Add syntax highlighting for `<--DO_NOT_TOUCH!-->` placeholders | Medium | #39 |
| 44 | Add keyboard shortcuts for efficient translation workflow | Low | #39 |
| 45 | Add bulk operations (approve/reject multiple) | Low | #38 |
| 46 | Responsive design / mobile support | Low | #38 |

### M5: Workflow Automation

| #  | Issue | Priority | Depends On |
|----|-------|----------|------------|
| 47 | Add CLI command: `sync` (fetch polish.txt from API) | Medium | M3 |
| 48 | Create BAT files: export, import, patch, run, full-workflow | Medium | #47 |
| 49 | Add CLI command: `import` (push exported.txt to API) | Medium | M3 |
| 50 | Update README with new workflow documentation | Low | M4 |
| 51 | Add Docker Compose for Web API + DB (dev environment) | Low | M3 |
| 52 | Create setup/first-run script (DB migration, initial import) | Medium | M2 |

---

## Recommended Execution Order

```
Sprint 1 (M1):  Issues #1-#11  — MediatR refactor
Sprint 2 (M2):  Issues #12-#24 — Database layer
Sprint 3 (M3):  Issues #25-#35 — Web API
Sprint 4 (M4):  Issues #36-#46 — Frontend
Sprint 5 (M5):  Issues #47-#52 — Automation & polish
```

Each milestone is independently deployable. After M1 the CLI still works identically.
After M2+M3 you can already use API for translations without frontend (Postman/curl).
M4 adds the UI. M5 ties everything together.

## Risk Notes

- **net10.0-windows + x86**: Web API (M3) runs on the same TFM only if DAT operations
  are needed server-side. If Web API only does DB CRUD, consider multi-targeting or
  separate `net10.0` (cross-platform) for WebApi project, `net10.0-windows` for CLI.
- **SQLite vs PostgreSQL**: SQLite is simplest for single-user local use. If multi-user
  or remote deployment needed → PostgreSQL.
- **Blazor WASM vs React**: Blazor keeps C# stack unified. React has better ecosystem
  for complex UIs. Decision point at issue #36.
