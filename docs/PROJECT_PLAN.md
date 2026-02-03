# LOTRO Polish Patcher - Project Plan

> Od MVP do platformy tłumaczeń. 6 milestones, 74 issues.

## Stan obecny (MVP)

- CLI export/patch działa, tłumaczenia w grze śmigają
- Launcher z update checking
- ~550 test assertions (unit + integration)
- Clean Architecture (5 warstw), Result monad, P/Invoke
- Brak MediatR — komendy to statyczne klasy
- Tłumaczenia w pliku pipe-delimited — edycja karkołomna
- Pola `approved` i `args_order` w formacie pliku, ale parser/patcher je ignorują (dead code)
- Single-user — każdy robi swoje tłumaczenia osobno

## Architektura docelowa

```
              ┌──────────────┐
              │  Web App UI  │  Blazor SSR
              │  net10.0     │  translation editor
              └──────┬───────┘
                     │ HTTP + JWT
 ┌───────────────────┼─────────────────────────────────────┐
 │                   │                                     │
 │  ┌────────────────▼──┐  ┌──────────────┐  ┌─────────┐  │
 │  │   Web API         │  │  Auth Server │  │  CLI    │  │
 │  │   ASP.NET Core    │  │  OpenIddict  │  │  win/x86│  │
 │  │   net10.0         │  │  net10.0     │  │         │  │
 │  └────────┬──────────┘  └──────────────┘  └────┬────┘  │
 │           │                                    │       │
 │  ┌────────▼────────────────────────────────────▼───┐   │
 │  │         IMediator (MediatR)                     │   │
 │  │      Commands / Queries / Handlers              │   │
 │  ├─────────────────────────────────────────────────┤   │
 │  │     LotroKoniecDev.Application  [net10.0]       │   │
 │  │   Handlers: Export, Patch, Translation CRUD     │   │
 │  │   Behaviors: Logging, Validation                │   │
 │  ├─────────────────────────────────────────────────┤   │
 │  │       LotroKoniecDev.Domain     [net10.0]       │   │
 │  │   Models, Result<T>, Errors, VOs                │   │
 │  ├──────────────────┬──────────────────────────────┤   │
 │  │  Infrastructure  │  Infrastructure              │   │
 │  │  .Persistence    │  .DatFile                    │   │
 │  │  [net10.0]       │  [net10.0-windows, x86]      │   │
 │  │  EF Core, MSSQL  │  P/Invoke, datexport.dll     │   │
 │  │  (API + CLI)     │  (CLI only)                  │   │
 │  └──────────────────┴──────────────────────────────┘   │
 └─────────────────────────────────────────────────────────┘
```

### Projekty w solution

| Projekt | TFM | Platform | Opis |
|---------|-----|----------|------|
| Primitives | `net10.0` | AnyCPU | Stałe, enumy |
| Domain | `net10.0` | AnyCPU | Modele, Result, Errors |
| Application | `net10.0` | AnyCPU | MediatR handlers, abstrakcje |
| Infrastructure.Persistence | `net10.0` | AnyCPU | EF Core, MSSQL, repozytoria |
| Infrastructure.DatFile | `net10.0-windows` | x86 | P/Invoke, datexport.dll |
| CLI | `net10.0-windows` | x86 | Presentation: CLI |
| WebApi | `net10.0` | AnyCPU | Presentation: REST API |
| Auth | `net10.0` | AnyCPU | OpenIddict auth server |
| WebApp | `net10.0` | AnyCPU | Presentation: Blazor SSR |

Obecny `Directory.Build.props` wymusza `net10.0-windows` + `x86` globalnie — trzeba
przejść na per-project. Infrastructure trzeba rozszczepić na .DatFile i .Persistence,
inaczej TFM mismatch blokuje Web API.

### Model tłumaczeń

Community-driven. Jeden centralny zestaw tłumaczeń per język. Każdy contribuuje,
admin per język zatwierdza.

- Multi-language schema od dnia 1. Aktywny development: Polish. Inne języki
  otwierane gdy pojawi się community.
- Prosty approval model: każdy z kontem edytuje/submituje, admin zatwierdza.
  Pełny review workflow (reviewer rola, states machine) → later.
- Glossary per język — spójna terminologia (np. "hobbit" → "niziołek" wszędzie).
- Export endpoint dostępny publicznie bez auth — CLI pobiera bez logowania.

### Workflow

```
1. CLI export  →  DAT → exported.txt (angielskie teksty)
2. Web App import  →  exported.txt → baza danych (English reference)
3. Tłumacz loguje się (OpenIddict), edytuje w Web App (side-by-side EN/PL)
4. Admin zatwierdza tłumaczenia
5. GET /api/export/{lang}.txt  →  polish.txt (public, no auth)
6. CLI patch  →  polish.txt → DAT
7. Odpal grę
```

BAT: `sync.bat && patch.bat && run.bat`

---

## M1: MediatR Clean Architecture Refactor

**Cel:** CLI jako cienki dispatcher — `IMediator.Send()`. Fundament pod resztę.

**Decyzje:**
- Handlers **zastępują** serwisy (Exporter, Patcher). `IExporter`/`IPatcher` → usunięte.
- Progress via `IProgress<T>` w DI — nie `Action<int,int>` callback.
- PreflightCheckQuery zwraca `PreflightReport` (dane) — zero `Console.ReadLine()`.

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

| #  | Issue | Priority | Depends On |
|----|-------|----------|------------|
| 1  | Restructure TFMs: split Infrastructure, per-project TFM/Platform | **CRITICAL** | — |
| 2  | Add MediatR NuGet packages | High | — |
| 3  | Design `IProgress<T>` pattern for handlers | High | — |
| 4  | ExportTextsQuery + Handler (zastępuje Exporter) | High | #2, #3 |
| 5  | ApplyPatchCommand + Handler (zastępuje Patcher) | High | #2, #3 |
| 6  | PreflightCheckQuery + Handler (dane, zero Console I/O) | Medium | #2 |
| 7  | LoggingPipelineBehavior | Medium | #2 |
| 8  | ValidationPipelineBehavior | Medium | #2 |
| 9  | Refactor CLI Program.cs → IMediator dispatch | High | #4, #5 |
| 10 | Delete IExporter, IPatcher, Exporter, Patcher, static Commands | High | #9 |
| 11 | Update DI registration (AddMediatR, behaviors) | High | #2 |
| 12 | Fix args reordering: wire ArgsOrder/ArgsId in patch pipeline (or remove) | Medium | — |
| 13 | Implement approved field: model, parser reads 6th field, patcher filters | Medium | — |
| 14 | Unit tests for MediatR handlers | High | #4, #5 |
| 15 | Integration tests for MediatR pipeline | Medium | #14 |

---

## M2: Database Layer (multi-language)

**Cel:** Flat file → MSSQL (code-first EF Core). Multi-language schema. CRUD, search,
historia edycji, glossary. Koniec z pipe-separated plikami.

**Dwa modele "Translation":**
- `Domain.Models.Translation` — init-only DTO dla DAT pipeline (FileId, GossipId, Content, `int[]?` ArgsOrder)
- `Persistence.Entities.TranslationEntity` — DB entity (Id, LanguageCode, timestamps, `string` ArgsOrder)
- Mapping w repository

**Schema (code-first EF Core → MSSQL, poniżej logiczny odpowiednik SQL):**
```sql
CREATE TABLE Languages (
    Code            NVARCHAR(10) PRIMARY KEY,     -- 'pl', 'de', 'fr'
    Name            NVARCHAR(100) NOT NULL,        -- 'Polish'
    IsActive        BIT NOT NULL DEFAULT 0
);

CREATE TABLE ExportedTexts (
    Id              INT IDENTITY(1,1) PRIMARY KEY,
    FileId          INT NOT NULL,
    GossipId        INT NOT NULL,
    EnglishContent  NVARCHAR(MAX) NOT NULL,
    Tag             NVARCHAR(200),                 -- ręczne tagowanie (nullable)
    ImportedAt      DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    CONSTRAINT UQ_ExportedTexts UNIQUE (FileId, GossipId)
);

CREATE TABLE Translations (
    Id              INT IDENTITY(1,1) PRIMARY KEY,
    FileId          INT NOT NULL,
    GossipId        INT NOT NULL,
    LanguageCode    NVARCHAR(10) NOT NULL,
    Content         NVARCHAR(MAX) NOT NULL,
    ArgsOrder       NVARCHAR(50),                  -- "1-2-3", NULL = default
    ArgsId          NVARCHAR(50),                  -- "1-2", NULL = default
    IsApproved      BIT NOT NULL DEFAULT 0,
    SubmittedById   INT,                           -- FK → Users (nullable pre-auth)
    ApprovedById    INT,                           -- FK → Users
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
    ChangedById     INT,                           -- FK → Users
    ChangedAt       DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    CONSTRAINT FK_History_Translation FOREIGN KEY (TranslationId) REFERENCES Translations(Id)
);

CREATE TABLE Glossary (
    Id              INT IDENTITY(1,1) PRIMARY KEY,
    LanguageCode    NVARCHAR(10) NOT NULL,
    EnglishTerm     NVARCHAR(500) NOT NULL,        -- 'hobbit', 'Shire'
    TranslatedTerm  NVARCHAR(500) NOT NULL,        -- 'niziołek', 'Shire'
    Context         NVARCHAR(500),                 -- 'race name', 'location'
    CONSTRAINT UQ_Glossary UNIQUE (LanguageCode, EnglishTerm),
    CONSTRAINT FK_Glossary_Language FOREIGN KEY (LanguageCode) REFERENCES Languages(Code)
);
```

User tables (`Users`, `UserRoles`) tworzone w M3 (Auth). FK z Translations → Users
dodane jako nullable teraz, enforced po M3.

| #  | Issue | Priority | Depends On |
|----|-------|----------|------------|
| 16 | Add EF Core + SQL Server NuGet packages | High | #1 |
| 17 | Schema design + TranslationEntity vs Translation DTO mapping | High | — |
| 18 | AppDbContext + entity configurations (Infrastructure.Persistence) | High | #16, #17 |
| 19 | ITranslationRepository abstraction (Application) | High | M1 |
| 20 | IExportedTextRepository abstraction (Application) | High | M1 |
| 21 | IGlossaryRepository abstraction (Application) | High | M1 |
| 22 | Implement repositories (Infrastructure.Persistence) | High | #18-#21 |
| 23 | EF migrations infrastructure | Medium | #18 |
| 24 | ImportExportedTextsCommand + Handler (exported.txt → DB) | High | #20, #22 |
| 25 | Translation CRUD Commands/Queries (MediatR, language-aware) | High | #19, #22 |
| 26 | ExportTranslationsQuery + Handler (DB → {lang}.txt, approved only) | High | #19, #22 |
| 27 | Glossary CRUD Commands/Queries | Medium | #21, #22 |
| 28 | Data migration: polish.txt → DB (one-time, approved=1, lang='pl') | Medium | #22, #25 |
| 29 | Exported text parser (parse export.txt, group by FileId) | High | #24 |
| 30 | Handle `\|\|` separator in content (escape/unescape) | Medium | #25 |
| 31 | Unit tests for repositories and handlers | High | #22, #25 |

---

## M3: Auth (OpenIddict)

**Cel:** Osobny projekt — OpenIddict authorization server. Portowany z istniejącego
projektu i dostosowany. Prosty role model: translator / admin per language.

**LotroKoniecDev.Auth** (`net10.0`):
- OpenIddict portowany z istniejącego projektu (konfiguracja, flows, token issuance)
- Dostosowanie: UserLanguageRoles, language-scoped policies
- Registration / login (email + password, lub OAuth providers later)
- Wydaje JWT access tokens
- User management API (admin)

**Role model:**
- `translator` — może tworzyć/edytować tłumaczenia (domyślna po rejestracji)
- `admin` — może zatwierdzać, zarządzać glossary, zarządzać użytkownikami
- Role per język — admin polskiego ≠ admin niemieckiego

**Schema (w tym samym DB co reszta, code-first EF Core → MSSQL):**
```sql
CREATE TABLE Users (
    Id              INT IDENTITY(1,1) PRIMARY KEY,
    Email           NVARCHAR(256) NOT NULL UNIQUE,
    DisplayName     NVARCHAR(100) NOT NULL,
    PasswordHash    NVARCHAR(MAX) NOT NULL,
    IsActive        BIT NOT NULL DEFAULT 1,
    CreatedAt       DATETIME2 NOT NULL DEFAULT GETUTCDATE()
);

CREATE TABLE UserLanguageRoles (
    UserId          INT NOT NULL,
    LanguageCode    NVARCHAR(10) NOT NULL,
    Role            NVARCHAR(50) NOT NULL,           -- 'translator', 'admin'
    CONSTRAINT PK_UserLanguageRoles PRIMARY KEY (UserId, LanguageCode),
    CONSTRAINT FK_ULR_User FOREIGN KEY (UserId) REFERENCES Users(Id),
    CONSTRAINT FK_ULR_Language FOREIGN KEY (LanguageCode) REFERENCES Languages(Code)
);

-- OpenIddict own tables (auto-created by OpenIddict EF Core):
-- OpenIddictApplications, OpenIddictAuthorizations,
-- OpenIddictScopes, OpenIddictTokens
```

**Web API integration:**
- JWT bearer auth middleware
- `[Authorize]` na write endpoints
- Export endpoint (`GET /api/export/{lang}.txt`) → public, no auth
- Role checks: `[Authorize(Policy = "LanguageAdmin")]` na approve/glossary

| #  | Issue | Priority | Depends On |
|----|-------|----------|------------|
| 32 | Port OpenIddict auth server from existing project, create LotroKoniecDev.Auth | High | #1 |
| 33 | Dostosuj OpenIddict config (JWT, flows, scopes) do tego projektu | High | #32 |
| 34 | Dostosuj registration + login endpoints | High | #33 |
| 35 | Users + UserLanguageRoles tables + EF config | High | #32, #18 |
| 36 | JWT token issuance (access + refresh) | High | #33 |
| 37 | Add JWT bearer auth to Web API | High | #36 |
| 38 | Role-based authorization policies (translator, admin per lang) | High | #37, #35 |
| 39 | Wire nullable User FKs in Translations/History (enforce after auth) | Medium | #35, #22 |
| 40 | User management: admin can list users, assign roles | Medium | #38 |
| 41 | Seed initial admin user (setup script) | Medium | #35 |
| 42 | Unit tests for auth + authorization | High | #38 |

---

## M4: Web API

**Cel:** REST API — drugi presentation layer obok CLI. Auth-aware. Language-aware.
Export endpoint public.

**Endpoints:**
```
-- Public (no auth)
GET    /api/export/{lang}.txt           Download approved translations
GET    /api/languages                   List active languages
GET    /api/glossary/{lang}             Language glossary

-- Authenticated (translator+)
GET    /api/translations                List (paginated, filterable, by language)
GET    /api/translations/{id}           Single
POST   /api/translations                Create (sets SubmittedById from JWT)
PUT    /api/translations/{id}           Update (own or admin)
GET    /api/translations/search?q=&lang= Full-text search EN + target language
GET    /api/translations/stats?lang=    Progress per language

-- Authenticated (admin)
PATCH  /api/translations/{id}/approve   Toggle approval (sets ApprovedById)
DELETE /api/translations/{id}           Delete
POST   /api/glossary/{lang}             Add glossary term
PUT    /api/glossary/{lang}/{id}        Update term
DELETE /api/glossary/{lang}/{id}        Delete term

-- Authenticated (admin)
POST   /api/import/exported-texts       Upload exported.txt → DB
POST   /api/import/translations         Upload legacy {lang}.txt → DB
GET    /api/exported-texts              List English texts
GET    /api/exported-texts/search?q=    Search English texts
```

| #  | Issue | Priority | Depends On |
|----|-------|----------|------------|
| 43 | Create LotroKoniecDev.WebApi project (net10.0) | High | #1, M2, M3 |
| 44 | DI, middleware, CORS, auth integration | High | #43 |
| 45 | TranslationsController (CRUD, language-aware, auth) | High | #43, #25 |
| 46 | ExportController (GET /{lang}.txt, public) | High | #43, #26 |
| 47 | ImportController (upload exported-texts, translations) | High | #43, #24 |
| 48 | SearchController (full-text search EN + target lang) | Medium | #43 |
| 49 | GlossaryController (CRUD, admin-only) | Medium | #43, #27 |
| 50 | StatsController (progress per language) | Medium | #43 |
| 51 | Pagination, filtering, sorting on list endpoints | Medium | #45 |
| 52 | Error handling middleware (Result → HTTP status) | Medium | #43 |
| 53 | Swagger/OpenAPI documentation | Low | #43 |
| 54 | Integration tests for API endpoints | High | #45, #46, #47 |

---

## M5: Web App (Frontend)

**Cel:** UI do tłumaczeń. Login, side-by-side editor, glossary, dashboard.

**Tech:** Blazor SSR (Server-Side Rendering). C# stack, shared Domain models,
server-side rendering z interactive components gdzie potrzeba (SignalR).

**Widoki:**
1. **Login / Register** — OpenIddict login flow
2. **Translation List** — tabela, search/filter/sort/lang, pagination, status coloring
3. **Translation Editor** — side-by-side EN/PL, placeholders jako kolorowe tagi,
   glossary hints (podświetl termin jeśli nie zgadza się z glossary), approve toggle (admin)
4. **File Browser** — group by FileId, full-text search po treści
5. **Glossary Editor** — CRUD terminów per język (admin)
6. **Dashboard** — progress bars per język, recent edits, top contributors
7. **Import/Export** — upload exported.txt, download {lang}.txt
8. **Admin Panel** — user management, role assignment

| #  | Issue | Priority | Depends On |
|----|-------|----------|------------|
| 55 | Create Blazor SSR project (net10.0) | High | M4 |
| 56 | Auth integration (login, token storage, refresh) | High | #55 |
| 57 | API client / HTTP service layer (auth headers) | High | #55, #56 |
| 58 | Translation List view | High | #57 |
| 59 | Translation Editor (side-by-side EN/PL, glossary hints) | High | #57 |
| 60 | File Browser (group by FileId, full-text search) | Medium | #57, #48 |
| 61 | Glossary Editor (admin) | Medium | #57, #49 |
| 62 | Dashboard (progress per lang, recent edits) | Medium | #57, #50 |
| 63 | Import/Export page | Medium | #57 |
| 64 | Syntax highlighting `<--DO_NOT_TOUCH!-->` + walidacja `\|\|` | Medium | #59 |
| 65 | Keyboard shortcuts (Ctrl+S, Ctrl+Enter, Alt+arrows) | Low | #59 |
| 66 | Bulk operations (approve/reject multiple, admin) | Low | #58 |
| 67 | Admin Panel (users, roles per lang) | Medium | #57, #40 |
| 68 | Responsive design / mobile | Low | #58 |

---

## M6: Workflow Automation

**Cel:** BAT files, CLI ↔ API sync, one-click workflow.

Export endpoint jest public — CLI pobiera bez logowania.

```bat
scripts/
  export.bat          CLI export (DAT → exported.txt)
  import.bat          Upload exported.txt do API (wymaga auth token)
  sync.bat            Download {lang}.txt z API (public)
  patch.bat           CLI patch ({lang}.txt → DAT)
  run.bat             Odpal LOTRO
  full-workflow.bat   sync → patch → run
  dev-start.bat       Start Auth + API + otwórz przeglądarkę
```

| #  | Issue | Priority | Depends On |
|----|-------|----------|------------|
| 69 | CLI command: `sync` (fetch {lang}.txt from API, public) | Medium | M4 |
| 70 | CLI command: `import` (push exported.txt to API, auth required) | Medium | M4 |
| 71 | BAT files: export, import, sync, patch, run, full-workflow | Medium | #69 |
| 72 | Update README with workflow documentation | Low | M5 |
| 73 | Docker Compose: Auth + API + MSSQL (CLI stays native Windows) | Low | M4 |
| 74 | Setup/first-run script (DB migration, seed admin, seed Polish lang) | Medium | M3 |

---

## Execution Order

```
Sprint 1 (M1):  #1-#15   TFM split + MediatR refactor + dead code cleanup
Sprint 2 (M2):  #16-#31  Database layer (multi-lang, glossary)
Sprint 3 (M3):  #32-#42  Auth (OpenIddict, users, roles)
Sprint 4 (M4):  #43-#54  Web API (auth-aware, language-aware)
Sprint 5 (M5):  #55-#68  Frontend
Sprint 6 (M6):  #69-#74  Automation
```

Issue #1 (TFM split) blokuje M2+. Zaczynaj od niego.

Każdy milestone deployowalny osobno:
- Po M1 → CLI działa identycznie
- Po M2 → baza gotowa, testy przechodzą
- Po M3 → auth server działa standalone
- Po M4 → API testowalne z Postman/Swagger
- Po M5 → UI gotowe, platforma działa
- Po M6 → one-click workflow

**74 issues total.**

## Podjęte decyzje

| Decyzja | Wybór |
|---------|-------|
| Baza danych | **MSSQL** (code-first EF Core) |
| Frontend | **Blazor SSR** |
| Auth | **OpenIddict** (portowany z istniejącego projektu) |
| Args reordering (#12) | Fix (do ustalenia przy implementacji) |

## Scope: later (nie w tym planie)

- AI review pipeline (LLM sprawdza placeholders, grammar, terminologię)
- Full review workflow (reviewer rola, states: submitted → in_review → approved)
- OAuth providers (GitHub, Discord) — na razie email+password via OpenIddict
- Notifications (email/Discord webhook na nowe submissions)
