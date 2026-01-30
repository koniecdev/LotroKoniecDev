#!/bin/bash
# =============================================================================
# LOTRO Polish Patcher - GitHub Issues Creator
# =============================================================================
# Usage: ./create-github-issues.sh
#
# Prerequisites:
#   - gh CLI installed and authenticated (gh auth login)
#   - Run from repo root or set REPO variable
#
# This script creates:
#   - 5 milestones
#   - Labels (epic, architecture, database, web-api, frontend, automation, testing)
#   - 52 issues with proper labels, milestones, and descriptions
# =============================================================================

set -euo pipefail

REPO="koniecdev/LotroKoniecDev"

echo "=== Creating Labels ==="
gh label create "epic"          --repo "$REPO" --color "7057ff" --description "Epic / large feature" --force
gh label create "architecture"  --repo "$REPO" --color "0075ca" --description "Architecture & refactoring" --force
gh label create "database"      --repo "$REPO" --color "006b75" --description "Database & persistence" --force
gh label create "web-api"       --repo "$REPO" --color "e4e669" --description "Web API backend" --force
gh label create "frontend"      --repo "$REPO" --color "d876e3" --description "Frontend / Web App" --force
gh label create "automation"    --repo "$REPO" --color "bfd4f2" --description "Workflow automation & scripts" --force
gh label create "testing"       --repo "$REPO" --color "c5def5" --description "Tests & quality" --force
gh label create "high-priority" --repo "$REPO" --color "b60205" --description "High priority" --force
gh label create "medium-priority" --repo "$REPO" --color "fbca04" --description "Medium priority" --force
gh label create "low-priority"  --repo "$REPO" --color "0e8a16" --description "Low priority" --force
gh label create "mediatr"       --repo "$REPO" --color "1d76db" --description "MediatR / CQRS" --force

echo ""
echo "=== Creating Milestones ==="
gh api repos/$REPO/milestones -f title="M1: MediatR Clean Architecture Refactor" -f description="Refactor CLI to CQRS with MediatR. Presentation layer becomes thin dispatcher. Foundation for Web API." -f state="open" 2>/dev/null || echo "Milestone M1 already exists"
gh api repos/$REPO/milestones -f title="M2: Translation Database Layer" -f description="Move from flat file (polish.txt) to SQLite database. Enable CRUD, search, metadata, edit history." -f state="open" 2>/dev/null || echo "Milestone M2 already exists"
gh api repos/$REPO/milestones -f title="M3: Web API" -f description="ASP.NET Core Web API as second presentation layer. Translation CRUD, import/export, search endpoints." -f state="open" 2>/dev/null || echo "Milestone M3 already exists"
gh api repos/$REPO/milestones -f title="M4: Translation Web App" -f description="User-friendly web UI for managing translations. Side-by-side EN/PL editor, quest browser, dashboard." -f state="open" 2>/dev/null || echo "Milestone M4 already exists"
gh api repos/$REPO/milestones -f title="M5: Workflow Automation" -f description="BAT files, CLI-API integration, Docker, documentation. End-to-end automated workflow." -f state="open" 2>/dev/null || echo "Milestone M5 already exists"

# Get milestone numbers
M1=$(gh api repos/$REPO/milestones --jq '.[] | select(.title | startswith("M1")) | .number')
M2=$(gh api repos/$REPO/milestones --jq '.[] | select(.title | startswith("M2")) | .number')
M3=$(gh api repos/$REPO/milestones --jq '.[] | select(.title | startswith("M3")) | .number')
M4=$(gh api repos/$REPO/milestones --jq '.[] | select(.title | startswith("M4")) | .number')
M5=$(gh api repos/$REPO/milestones --jq '.[] | select(.title | startswith("M5")) | .number')

echo ""
echo "Milestones: M1=$M1, M2=$M2, M3=$M3, M4=$M4, M5=$M5"
echo ""

# Helper function
create_issue() {
    local title="$1"
    local body="$2"
    local labels="$3"
    local milestone="$4"

    echo "Creating: $title"
    gh issue create \
        --repo "$REPO" \
        --title "$title" \
        --body "$body" \
        --label "$labels" \
        --milestone "$milestone"
    sleep 0.5  # Rate limiting
}

echo "=== M1: MediatR Clean Architecture Refactor ==="

create_issue \
    "[M1] Add MediatR NuGet packages to solution" \
    "$(cat <<'EOF'
## Description
Add MediatR and MediatR.Extensions.Microsoft.DependencyInjection to the solution.

## Tasks
- [ ] Add `MediatR` package to `Directory.Packages.props`
- [ ] Add `MediatR.Extensions.Microsoft.DependencyInjection` package
- [ ] Reference MediatR in `LotroKoniecDev.Application.csproj`
- [ ] Reference MediatR DI extensions in `LotroKoniecDev.csproj` (CLI)
- [ ] Verify solution builds

## Acceptance Criteria
- `IMediator`, `IRequest<T>`, `IRequestHandler<,>`, `IPipelineBehavior<,>` are available in Application layer
- Solution compiles without errors

## Architecture Notes
MediatR lives in **Application layer** (handlers). Presentation layers (CLI, future Web API) only reference `IMediator` to dispatch.
EOF
)" \
    "architecture,mediatr,high-priority" \
    "$M1"

create_issue \
    "[M1] Create ExportTextsQuery + Handler (CQRS)" \
    "$(cat <<'EOF'
## Description
Create MediatR query to replace direct `IExporter` usage. Encapsulate export logic in a handler.

## Tasks
- [ ] Create `Application/Features/Export/ExportTextsQuery.cs` implementing `IRequest<Result<ExportSummary>>`
  - Properties: `DatFilePath`, `OutputPath`, `Progress` callback
- [ ] Create `Application/Features/Export/ExportTextsQueryHandler.cs` implementing `IRequestHandler<ExportTextsQuery, Result<ExportSummary>>`
  - Move logic from current `Exporter` class into handler
  - Inject `IDatFileHandler` via constructor
- [ ] Keep existing `Exporter` temporarily (for backward compat during migration)
- [ ] Register handler via `AddMediatR()` assembly scanning

## Acceptance Criteria
- `IMediator.Send(new ExportTextsQuery { ... })` returns `Result<ExportSummary>`
- Existing `ExportCommand.Run()` can be refactored to use IMediator

## Depends On
- #1 (MediatR packages)
EOF
)" \
    "architecture,mediatr,high-priority" \
    "$M1"

create_issue \
    "[M1] Create ApplyPatchCommand + Handler (CQRS)" \
    "$(cat <<'EOF'
## Description
Create MediatR command to replace direct `IPatcher` usage. The handler orchestrates the full patch workflow.

## Tasks
- [ ] Create `Application/Features/Patch/ApplyPatchCommand.cs` implementing `IRequest<Result<PatchSummary>>`
  - Properties: `TranslationsPath`, `DatFilePath`, `Progress` callback
- [ ] Create `Application/Features/Patch/ApplyPatchCommandHandler.cs`
  - Inject `IPatcher`, `ITranslationParser`, `IDatFileHandler`
  - Move core patch orchestration into handler
- [ ] Keep backup/restore logic in CLI layer (presentation concern)
- [ ] Register handler via assembly scanning

## Acceptance Criteria
- `IMediator.Send(new ApplyPatchCommand { ... })` returns `Result<PatchSummary>`
- Handler focuses on business logic, CLI handles user interaction

## Depends On
- #1 (MediatR packages)
EOF
)" \
    "architecture,mediatr,high-priority" \
    "$M1"

create_issue \
    "[M1] Create PreflightCheckQuery + Handler" \
    "$(cat <<'EOF'
## Description
Move preflight checks (game running, write access, version check) into MediatR query.

## Tasks
- [ ] Create `Application/Features/Patch/PreflightCheckQuery.cs` implementing `IRequest<Result<PreflightReport>>`
  - Properties: `DatFilePath`, `VersionFilePath`
- [ ] Create `PreflightReport` record: `GameRunning`, `HasWriteAccess`, `UpdateAvailable`, `CanProceed`
- [ ] Create handler injecting `IGameProcessDetector`, `IWriteAccessChecker`, `IGameUpdateChecker`
- [ ] Move logic from current `PreflightChecker` static class

## Acceptance Criteria
- Preflight checks are reusable between CLI and future Web API
- `PreflightReport` provides all info for presentation layer to decide/prompt user

## Depends On
- #1 (MediatR packages)
EOF
)" \
    "architecture,mediatr,medium-priority" \
    "$M1"

create_issue \
    "[M1] Add LoggingPipelineBehavior" \
    "$(cat <<'EOF'
## Description
Cross-cutting logging via MediatR pipeline behavior. Logs every request/response.

## Tasks
- [ ] Create `Application/Behaviors/LoggingPipelineBehavior.cs` implementing `IPipelineBehavior<TRequest, TResponse>`
- [ ] Log request type name, timestamp on entry
- [ ] Log result status (success/failure), duration on exit
- [ ] Use `ILogger<T>` or simple console output initially
- [ ] Register in DI as open generic

## Acceptance Criteria
- Every MediatR request is logged with timing info
- Failures include error details in log

## Depends On
- #1 (MediatR packages)
EOF
)" \
    "architecture,mediatr,medium-priority" \
    "$M1"

create_issue \
    "[M1] Add ValidationPipelineBehavior" \
    "$(cat <<'EOF'
## Description
Validation pipeline that runs before handlers. Uses FluentValidation or manual validators.

## Tasks
- [ ] Create `Application/Behaviors/ValidationPipelineBehavior.cs`
- [ ] Create `IValidator<T>` abstraction (or use FluentValidation)
- [ ] Create validators for `ExportTextsQuery` (valid paths) and `ApplyPatchCommand` (files exist)
- [ ] Return `Result.Failure()` on validation errors (no exceptions)
- [ ] Register in DI

## Acceptance Criteria
- Invalid requests are rejected before reaching handler
- Validation errors use existing `Result` monad (railway-oriented)

## Depends On
- #1 (MediatR packages)
EOF
)" \
    "architecture,mediatr,medium-priority" \
    "$M1"

create_issue \
    "[M1] Refactor CLI Program.cs to use IMediator dispatch" \
    "$(cat <<'EOF'
## Description
CLI becomes a thin dispatcher. `Program.cs` resolves `IMediator`, sends commands/queries, handles presentation.

## Tasks
- [ ] Register `AddMediatR(cfg => cfg.RegisterServicesFromAssembly(...))` in DI setup
- [ ] Refactor `ExportCommand` to resolve `IMediator` and send `ExportTextsQuery`
- [ ] Refactor `PatchCommand` to resolve `IMediator` and send `PreflightCheckQuery` + `ApplyPatchCommand`
- [ ] CLI only handles: arg parsing, console output, exit codes, backup/restore
- [ ] Test end-to-end CLI flow with MediatR

## Acceptance Criteria
- CLI behavior is identical to current (no user-visible changes)
- All business logic flows through MediatR pipeline
- Command classes no longer directly reference `IExporter`, `IPatcher`

## Depends On
- #2 (ExportTextsQuery), #3 (ApplyPatchCommand)
EOF
)" \
    "architecture,mediatr,high-priority" \
    "$M1"

create_issue \
    "[M1] Remove legacy static Command classes" \
    "$(cat <<'EOF'
## Description
Clean up: remove old static command pattern after MediatR migration is complete.

## Tasks
- [ ] Delete or refactor `ExportCommand` static class → thin CLI handler
- [ ] Delete or refactor `PatchCommand` static class → thin CLI handler
- [ ] Delete `PreflightChecker` static class (replaced by MediatR handler)
- [ ] Ensure `BackupManager` stays in CLI layer (presentation concern)
- [ ] Ensure no dead code remains

## Acceptance Criteria
- No business logic in CLI layer (only arg parsing, output, exit codes)
- All static command classes removed or reduced to thin dispatchers

## Depends On
- #7 (CLI refactored to IMediator)
EOF
)" \
    "architecture,mediatr,high-priority" \
    "$M1"

create_issue \
    "[M1] Update DI registration for MediatR" \
    "$(cat <<'EOF'
## Description
Update `ApplicationDependencyInjection.cs` to register MediatR services, handlers, and behaviors.

## Tasks
- [ ] Add `services.AddMediatR(cfg => { cfg.RegisterServicesFromAssembly(typeof(ExportTextsQuery).Assembly); })`
- [ ] Register pipeline behaviors in order: Logging → Validation → Handler
- [ ] Optionally keep `IExporter`/`IPatcher` registrations if handlers delegate to them
- [ ] Or remove them if handlers contain the logic directly
- [ ] Verify all handlers are discovered by assembly scanning

## Acceptance Criteria
- All MediatR handlers resolve correctly
- Pipeline behaviors execute in correct order

## Depends On
- #1 (MediatR packages)
EOF
)" \
    "architecture,mediatr,high-priority" \
    "$M1"

create_issue \
    "[M1] Update unit tests for MediatR handlers" \
    "$(cat <<'EOF'
## Description
Write unit tests for all MediatR handlers. Mock dependencies via NSubstitute.

## Tasks
- [ ] Create `ExportTextsQueryHandlerTests` — mock IDatFileHandler, verify Result
- [ ] Create `ApplyPatchCommandHandlerTests` — mock IPatcher, ITranslationParser
- [ ] Create `PreflightCheckQueryHandlerTests` — mock detectors/checkers
- [ ] Create `LoggingPipelineBehaviorTests` — verify logging calls
- [ ] Create `ValidationPipelineBehaviorTests` — verify rejection on invalid input
- [ ] Maintain FluentAssertions + NSubstitute patterns

## Acceptance Criteria
- All handlers have ≥90% coverage
- Tests follow existing `MethodName_Scenario_ExpectedResult` naming

## Depends On
- #2 (ExportTextsQuery), #3 (ApplyPatchCommand)
EOF
)" \
    "testing,mediatr,high-priority" \
    "$M1"

create_issue \
    "[M1] Update integration tests for MediatR pipeline" \
    "$(cat <<'EOF'
## Description
Integration tests verifying full MediatR pipeline (behavior → handler → result).

## Tasks
- [ ] Test full export pipeline with mocked DAT handler
- [ ] Test full patch pipeline with mocked DAT handler
- [ ] Test validation behavior rejects invalid requests
- [ ] Test logging behavior produces output
- [ ] Verify DI container resolves all handlers correctly

## Acceptance Criteria
- Integration tests pass with full MediatR pipeline
- Pipeline behaviors execute in correct order

## Depends On
- #10 (Unit tests)
EOF
)" \
    "testing,mediatr,medium-priority" \
    "$M1"

echo ""
echo "=== M2: Translation Database Layer ==="

create_issue \
    "[M2] Add EF Core + SQLite NuGet packages" \
    "$(cat <<'EOF'
## Description
Add Entity Framework Core with SQLite provider to the solution.

## Tasks
- [ ] Add to `Directory.Packages.props`:
  - `Microsoft.EntityFrameworkCore`
  - `Microsoft.EntityFrameworkCore.Sqlite`
  - `Microsoft.EntityFrameworkCore.Design` (tools)
  - `Microsoft.EntityFrameworkCore.Tools` (migrations)
- [ ] Reference EF Core in `LotroKoniecDev.Infrastructure.csproj`
- [ ] Reference EF Core abstractions in `LotroKoniecDev.Application.csproj` (if needed for IQueryable)
- [ ] Verify build

## Acceptance Criteria
- EF Core available in Infrastructure layer
- Solution compiles
EOF
)" \
    "database,high-priority" \
    "$M2"

create_issue \
    "[M2] Design database schema" \
    "$(cat <<'EOF'
## Description
Design and document the database schema for translations, exported texts, and audit history.

## Tables
```sql
ExportedTexts: FileId, GossipId, EnglishContent, QuestTitle, Category, ImportedAt
Translations: FileId, GossipId, PolishContent, ArgsOrder, ArgsId, IsApproved, CreatedAt, UpdatedAt, Notes
TranslationHistory: TranslationId, OldContent, NewContent, ChangedAt
```

## Tasks
- [ ] Finalize schema design (see PROJECT_PLAN.md for draft)
- [ ] Define indexes (FileId+GossipId composite unique)
- [ ] Define foreign key relationships
- [ ] Document in migration or design doc
- [ ] Consider full-text search index for content fields

## Acceptance Criteria
- Schema supports CRUD, search, history tracking
- Composite unique constraint on (FileId, GossipId) for both tables
EOF
)" \
    "database,high-priority" \
    "$M2"

create_issue \
    "[M2] Create AppDbContext + entity configurations" \
    "$(cat <<'EOF'
## Description
Create EF Core DbContext with entity configurations (Fluent API).

## Tasks
- [ ] Create `Infrastructure/Persistence/AppDbContext.cs`
  - `DbSet<ExportedTextEntity> ExportedTexts`
  - `DbSet<TranslationEntity> Translations`
  - `DbSet<TranslationHistoryEntity> TranslationHistory`
- [ ] Create entity classes in `Infrastructure/Persistence/Entities/`
- [ ] Create Fluent API configurations in `Infrastructure/Persistence/Configurations/`
- [ ] Configure composite unique indexes, relationships, value conversions
- [ ] Register `AppDbContext` in `InfrastructureDependencyInjection.cs`

## Acceptance Criteria
- DbContext correctly maps to SQLite database
- All constraints and indexes defined via Fluent API

## Depends On
- #12 (EF Core packages), #13 (Schema design)
EOF
)" \
    "database,high-priority" \
    "$M2"

create_issue \
    "[M2] Create ITranslationRepository abstraction" \
    "$(cat <<'EOF'
## Description
Repository abstraction in Application layer for translation CRUD operations.

## Tasks
- [ ] Create `Application/Abstractions/ITranslationRepository.cs`
  - `Task<Result<Translation>> GetByIdAsync(int id)`
  - `Task<Result<IReadOnlyList<Translation>>> GetAllAsync(TranslationFilter? filter)`
  - `Task<Result<Translation>> GetByFileAndGossipIdAsync(int fileId, int gossipId)`
  - `Task<Result<int>> CreateAsync(Translation translation)`
  - `Task<Result> UpdateAsync(Translation translation)`
  - `Task<Result> DeleteAsync(int id)`
  - `Task<Result> SetApprovalAsync(int id, bool approved)`
  - `Task<Result<TranslationStats>> GetStatsAsync()`
- [ ] Create `TranslationFilter` record (search query, approval status, pagination)
- [ ] Create `TranslationStats` record (total, translated, approved, pending)

## Acceptance Criteria
- Abstraction uses Result<T> monad (consistent with codebase patterns)
- No EF Core references in Application layer

## Depends On
- M1 completed (MediatR foundation)
EOF
)" \
    "database,architecture,high-priority" \
    "$M2"

create_issue \
    "[M2] Create IExportedTextRepository abstraction" \
    "$(cat <<'EOF'
## Description
Repository abstraction for storing and querying exported English texts (reference data).

## Tasks
- [ ] Create `Application/Abstractions/IExportedTextRepository.cs`
  - `Task<Result> BulkImportAsync(IReadOnlyList<ExportedText> texts)`
  - `Task<Result<IReadOnlyList<ExportedText>>> SearchAsync(string query)`
  - `Task<Result<ExportedText>> GetByFileAndGossipIdAsync(int fileId, int gossipId)`
  - `Task<Result<IReadOnlyList<ExportedText>>> GetAllAsync(ExportedTextFilter? filter)`
- [ ] Create `ExportedText` domain model (FileId, GossipId, EnglishContent, QuestTitle, Category)
- [ ] Create `ExportedTextFilter` record

## Acceptance Criteria
- Supports bulk import (thousands of records from export.txt)
- Search covers EnglishContent and QuestTitle fields

## Depends On
- M1 completed
EOF
)" \
    "database,architecture,high-priority" \
    "$M2"

create_issue \
    "[M2] Implement repositories in Infrastructure layer" \
    "$(cat <<'EOF'
## Description
Concrete EF Core implementations of repository abstractions.

## Tasks
- [ ] Create `Infrastructure/Persistence/Repositories/TranslationRepository.cs`
- [ ] Create `Infrastructure/Persistence/Repositories/ExportedTextRepository.cs`
- [ ] Implement all CRUD operations with EF Core
- [ ] Use `AsNoTracking()` for read queries
- [ ] Implement search with `EF.Functions.Like()` or full-text
- [ ] Handle concurrency (optimistic locking via `UpdatedAt`)
- [ ] Auto-create `TranslationHistory` entries on update
- [ ] Register repositories in DI (Scoped lifetime)

## Acceptance Criteria
- All repository methods return Result<T>
- Bulk import handles 50k+ records efficiently
- History tracked on every translation update

## Depends On
- #14 (DbContext), #15 (ITranslationRepository), #16 (IExportedTextRepository)
EOF
)" \
    "database,high-priority" \
    "$M2"

create_issue \
    "[M2] Create EF migrations infrastructure" \
    "$(cat <<'EOF'
## Description
Set up EF Core migrations for database versioning.

## Tasks
- [ ] Configure design-time DbContext factory
- [ ] Create initial migration (CreateDatabase)
- [ ] Add migration for full-text search indexes (if using SQLite FTS5)
- [ ] Add auto-migration on app startup (development mode)
- [ ] Document migration commands in README

## Acceptance Criteria
- `dotnet ef database update` creates clean database
- Migrations are versioned and repeatable

## Depends On
- #14 (DbContext)
EOF
)" \
    "database,medium-priority" \
    "$M2"

create_issue \
    "[M2] Create ImportExportedTextsCommand + Handler" \
    "$(cat <<'EOF'
## Description
MediatR command to import exported.txt (English texts from DAT) into database.

## Tasks
- [ ] Create `Application/Features/Import/ImportExportedTextsCommand.cs`
  - Properties: `FilePath` (path to exported.txt)
- [ ] Create handler that:
  1. Parses exported.txt format
  2. Extracts FileId, GossipId, EnglishContent
  3. Attempts to extract QuestTitle from content heuristics
  4. Bulk upserts into ExportedTexts table
- [ ] Handle re-import (upsert, not duplicate)
- [ ] Report: new texts, updated texts, unchanged

## Acceptance Criteria
- Importing 50k+ texts completes in reasonable time
- Re-importing same file is idempotent

## Depends On
- #16 (IExportedTextRepository), #17 (Repository impl)
EOF
)" \
    "database,high-priority" \
    "$M2"

create_issue \
    "[M2] Create Translation CRUD Commands/Queries (MediatR)" \
    "$(cat <<'EOF'
## Description
Full CRUD via MediatR for translations. These are the core operations the Web API will expose.

## Tasks
- [ ] `GetTranslationsQuery` + Handler (paginated, filterable list)
- [ ] `GetTranslationByIdQuery` + Handler
- [ ] `CreateTranslationCommand` + Handler
- [ ] `UpdateTranslationCommand` + Handler (with history tracking)
- [ ] `DeleteTranslationCommand` + Handler
- [ ] `SetTranslationApprovalCommand` + Handler
- [ ] `SearchTranslationsQuery` + Handler (full-text across EN/PL)
- [ ] `GetTranslationStatsQuery` + Handler (dashboard data)
- [ ] Validators for create/update commands

## Acceptance Criteria
- All operations return Result<T>
- Update creates TranslationHistory entry
- Search works across English reference and Polish translation

## Depends On
- #15 (ITranslationRepository), #17 (Repository impl)
EOF
)" \
    "database,mediatr,high-priority" \
    "$M2"

create_issue \
    "[M2] Create ExportTranslationsQuery + Handler (DB to polish.txt)" \
    "$(cat <<'EOF'
## Description
MediatR query that exports approved translations from database to polish.txt format.

## Tasks
- [ ] Create `Application/Features/Export/ExportTranslationsQuery.cs`
  - Properties: `OnlyApproved` (bool, default true), `OutputPath` (optional)
- [ ] Create handler that:
  1. Fetches translations from repository
  2. Formats in `file_id||gossip_id||text||args_order||args_id||approved` format
  3. Sorts by FileId then GossipId
  4. Returns as string or writes to file
- [ ] Reuse existing format logic from TranslationFileParser (reverse direction)

## Acceptance Criteria
- Generated polish.txt is compatible with existing CLI `patch` command
- Only approved translations included by default
- Output matches existing file format exactly

## Depends On
- #15, #17 (Repository)
EOF
)" \
    "database,mediatr,high-priority" \
    "$M2"

create_issue \
    "[M2] Data migration tool: polish.txt to database" \
    "$(cat <<'EOF'
## Description
One-time migration of existing polish.txt translations into the new database.

## Tasks
- [ ] Create CLI command or utility: `migrate` or `import-legacy`
- [ ] Parse existing polish.txt using `ITranslationParser`
- [ ] Insert all translations into database
- [ ] Mark all as approved (they were in production)
- [ ] Report migration summary

## Acceptance Criteria
- All existing translations preserved in database
- No data loss during migration
- Can run multiple times safely (upsert)

## Depends On
- #17, #20 (Repositories and CRUD)
EOF
)" \
    "database,medium-priority" \
    "$M2"

create_issue \
    "[M2] Add exported text parser (export.txt to structured data)" \
    "$(cat <<'EOF'
## Description
Parser for the exported.txt file format (output of DAT export). Needed for importing English reference texts.

## Tasks
- [ ] Analyze export.txt format (output of `Exporter.ExportAllTexts`)
- [ ] Create `Application/Parsers/ExportedTextParser.cs`
- [ ] Parse FileId, GossipId, and text content
- [ ] Extract quest titles from content (heuristic: first line, specific patterns)
- [ ] Categorize entries (quest, item, NPC, UI text) by FileId ranges or content patterns
- [ ] Return `Result<IReadOnlyList<ExportedText>>`

## Acceptance Criteria
- Parses full export.txt without errors
- Extracts meaningful quest titles where possible

## Depends On
- #19 (ImportExportedTextsCommand)
EOF
)" \
    "database,high-priority" \
    "$M2"

create_issue \
    "[M2] Unit tests for repositories and database handlers" \
    "$(cat <<'EOF'
## Description
Tests for all database operations using in-memory SQLite.

## Tasks
- [ ] Create test fixture with in-memory SQLite DbContext
- [ ] Test TranslationRepository: CRUD, search, pagination, stats
- [ ] Test ExportedTextRepository: bulk import, search
- [ ] Test TranslationHistory auto-creation on update
- [ ] Test ExportTranslationsQueryHandler output format
- [ ] Test ImportExportedTextsCommand with sample data
- [ ] Test data migration tool

## Acceptance Criteria
- All repository operations tested
- Edge cases covered (duplicate keys, empty results, large datasets)

## Depends On
- #17, #20, #21 (Implementations)
EOF
)" \
    "testing,database,high-priority" \
    "$M2"

echo ""
echo "=== M3: Web API ==="

create_issue \
    "[M3] Create LotroKoniecDev.WebApi project" \
    "$(cat <<'EOF'
## Description
New ASP.NET Core Web API project — second presentation layer alongside CLI.

## Tasks
- [ ] Create `src/LotroKoniecDev.WebApi/` project
- [ ] Target `net10.0` (cross-platform, no Windows dependency for DB-only API)
- [ ] Add project to `LotroKoniecDev.slnx`
- [ ] Reference `LotroKoniecDev.Application` and `LotroKoniecDev.Infrastructure`
- [ ] Configure `Program.cs` with:
  - MediatR registration
  - Application + Infrastructure DI
  - EF Core + SQLite
  - CORS, JSON options, error handling
- [ ] Add `appsettings.json` with connection string and config

## Architecture Decision
Web API does NOT need `net10.0-windows` or x86 if it only does DB operations.
DAT file operations stay in CLI. Web API = translation management only.

## Acceptance Criteria
- `dotnet run --project src/LotroKoniecDev.WebApi` starts API on localhost:5000
- Swagger UI available at /swagger

## Depends On
- M1 (MediatR), M2 (Database)
EOF
)" \
    "web-api,architecture,high-priority" \
    "$M3"

create_issue \
    "[M3] Configure DI, middleware, CORS in WebApi" \
    "$(cat <<'EOF'
## Description
Set up the Web API middleware pipeline and dependency injection.

## Tasks
- [ ] Configure CORS (allow localhost dev, configurable origins)
- [ ] Add global exception handling middleware
- [ ] Add Result<T> → HTTP status code mapping (Success→200, NotFound→404, Failure→400/500)
- [ ] Configure JSON serialization (camelCase, nullable handling)
- [ ] Add request logging middleware
- [ ] Configure Swagger with operation descriptions

## Acceptance Criteria
- CORS allows frontend dev server
- Errors return consistent JSON format
- Result.Failure maps to appropriate HTTP status
EOF
)" \
    "web-api,high-priority" \
    "$M3"

create_issue \
    "[M3] Create TranslationsController (CRUD endpoints)" \
    "$(cat <<'EOF'
## Description
REST API controller for translation CRUD operations.

## Endpoints
```
GET    /api/translations              List (paginated, filterable)
GET    /api/translations/{id}         Get single
POST   /api/translations              Create
PUT    /api/translations/{id}         Update
DELETE /api/translations/{id}         Delete
PATCH  /api/translations/{id}/approve Toggle approval
```

## Tasks
- [ ] Create `Controllers/TranslationsController.cs`
- [ ] Inject `IMediator`
- [ ] Map each endpoint to corresponding MediatR command/query
- [ ] Add request DTOs (CreateTranslationRequest, UpdateTranslationRequest)
- [ ] Add response DTOs (TranslationResponse, PagedResponse<T>)
- [ ] Add `[ProducesResponseType]` attributes for Swagger

## Acceptance Criteria
- Full CRUD works via Swagger
- Pagination with page/pageSize parameters
- Filter by approval status, search query

## Depends On
- #25 (WebApi project), #20 (CRUD commands/queries)
EOF
)" \
    "web-api,high-priority" \
    "$M3"

create_issue \
    "[M3] Create ImportController (import endpoints)" \
    "$(cat <<'EOF'
## Description
Endpoints for importing exported.txt and legacy polish.txt into database.

## Endpoints
```
POST /api/import/exported-texts    Upload exported.txt → DB (English reference)
POST /api/import/translations      Upload polish.txt → DB (legacy migration)
```

## Tasks
- [ ] Create `Controllers/ImportController.cs`
- [ ] Accept file upload (`IFormFile`)
- [ ] Dispatch to `ImportExportedTextsCommand` and migration handler
- [ ] Return import summary (new, updated, errors)
- [ ] Add file size limits and validation

## Acceptance Criteria
- Upload 50k+ line exported.txt successfully
- Import summary returned in response
- Re-upload is idempotent (upsert)

## Depends On
- #25 (WebApi project), #19, #22 (Import handlers)
EOF
)" \
    "web-api,high-priority" \
    "$M3"

create_issue \
    "[M3] Create ExportController (download polish.txt)" \
    "$(cat <<'EOF'
## Description
Endpoint to download approved translations as polish.txt file.

## Endpoints
```
GET /api/export/polish.txt         Download polish.txt (approved translations)
GET /api/export/polish.txt?all=true Include unapproved too
```

## Tasks
- [ ] Create `Controllers/ExportController.cs`
- [ ] Dispatch to `ExportTranslationsQuery`
- [ ] Return as file download (Content-Type: text/plain, Content-Disposition: attachment)
- [ ] Optional `all` parameter to include unapproved
- [ ] Set appropriate cache headers

## Acceptance Criteria
- Downloaded file is compatible with CLI `patch` command
- File format matches existing polish.txt exactly

## Depends On
- #25 (WebApi project), #21 (ExportTranslationsQuery)
EOF
)" \
    "web-api,high-priority" \
    "$M3"

create_issue \
    "[M3] Create SearchController (full-text search)" \
    "$(cat <<'EOF'
## Description
Search endpoint for finding texts across English exports and Polish translations.

## Endpoints
```
GET /api/translations/search?q=hobbit    Search Polish translations
GET /api/exported-texts/search?q=quest   Search English reference texts
GET /api/search?q=gandalf                Combined search (both)
```

## Tasks
- [ ] Create search endpoints (could be part of existing controllers or separate)
- [ ] Dispatch to `SearchTranslationsQuery`
- [ ] Return matched texts with context (file ID, gossip ID, both EN and PL if available)
- [ ] Support minimum query length (3+ chars)
- [ ] Consider SQLite FTS5 for performance

## Acceptance Criteria
- Search across 50k+ entries returns in <500ms
- Results include both English reference and Polish translation side-by-side

## Depends On
- #25 (WebApi project)
EOF
)" \
    "web-api,medium-priority" \
    "$M3"

create_issue \
    "[M3] Add pagination, filtering, sorting to list endpoints" \
    "$(cat <<'EOF'
## Description
Standard REST patterns for list endpoints.

## Tasks
- [ ] Add `PagedRequest` base: `Page`, `PageSize`, `SortBy`, `SortDirection`
- [ ] Add `PagedResponse<T>`: `Items`, `TotalCount`, `Page`, `PageSize`, `TotalPages`
- [ ] Apply to Translations list and ExportedTexts list
- [ ] Filter by: `IsApproved`, `HasTranslation`, `Category`, `SearchQuery`
- [ ] Sort by: `FileId`, `GossipId`, `UpdatedAt`, `QuestTitle`
- [ ] Default: page=1, pageSize=50, sort=UpdatedAt desc

## Acceptance Criteria
- Large datasets are efficiently paginated
- Filters combinable (approved + search + category)

## Depends On
- #27 (TranslationsController)
EOF
)" \
    "web-api,medium-priority" \
    "$M3"

create_issue \
    "[M3] Add Swagger/OpenAPI documentation" \
    "$(cat <<'EOF'
## Description
Auto-generated API documentation via Swagger/OpenAPI.

## Tasks
- [ ] Add `Swashbuckle.AspNetCore` or `NSwag` package
- [ ] Configure XML documentation generation
- [ ] Add `[ProducesResponseType]` to all endpoints
- [ ] Add operation descriptions and examples
- [ ] Group endpoints by tag (Translations, Import, Export, Search)

## Acceptance Criteria
- Swagger UI at /swagger shows all endpoints with examples
- Can test endpoints directly from Swagger UI

## Depends On
- #25 (WebApi project)
EOF
)" \
    "web-api,low-priority" \
    "$M3"

create_issue \
    "[M3] Add StatsController (dashboard data)" \
    "$(cat <<'EOF'
## Description
Endpoint providing translation progress statistics for the dashboard.

## Endpoints
```
GET /api/stats    Returns translation progress data
```

## Response
```json
{
  "totalExportedTexts": 52000,
  "totalTranslations": 12500,
  "approvedTranslations": 11000,
  "pendingTranslations": 1500,
  "translationPercentage": 24.04,
  "approvalPercentage": 88.00,
  "recentEdits": [...]
}
```

## Tasks
- [ ] Create `Controllers/StatsController.cs`
- [ ] Dispatch to `GetTranslationStatsQuery`
- [ ] Include recent edit activity (last 10 edits)
- [ ] Cache stats for 1 minute (avoid expensive count queries)

## Depends On
- #25 (WebApi project)
EOF
)" \
    "web-api,medium-priority" \
    "$M3"

create_issue \
    "[M3] Integration tests for API endpoints" \
    "$(cat <<'EOF'
## Description
API integration tests using WebApplicationFactory.

## Tasks
- [ ] Create `tests/LotroKoniecDev.Tests.Api/` project
- [ ] Add `Microsoft.AspNetCore.Mvc.Testing` package
- [ ] Create test fixture with in-memory SQLite
- [ ] Test TranslationsController: CRUD, pagination, search
- [ ] Test ImportController: file upload, bulk import
- [ ] Test ExportController: file download, format validation
- [ ] Test error responses (404, 400, 500)

## Acceptance Criteria
- All API endpoints tested
- Response format and status codes verified
- File upload/download tested

## Depends On
- #27, #28, #29 (Controllers)
EOF
)" \
    "testing,web-api,high-priority" \
    "$M3"

create_issue \
    "[M3] Add error handling middleware (Result to HTTP mapping)" \
    "$(cat <<'EOF'
## Description
Middleware that maps Result<T> failures to appropriate HTTP responses.

## Mapping
```
ErrorType.Validation → 400 Bad Request
ErrorType.NotFound   → 404 Not Found
ErrorType.Failure    → 500 Internal Server Error
ErrorType.IoError    → 500 Internal Server Error
```

## Tasks
- [ ] Create `Middleware/ResultToHttpMiddleware.cs` or use ActionFilter
- [ ] Map Error types to HTTP status codes
- [ ] Return consistent error JSON: `{ "error": { "code": "...", "message": "..." } }`
- [ ] Log failures server-side
- [ ] Don't expose internal details in production

## Acceptance Criteria
- All error responses have consistent format
- No stack traces in production responses

## Depends On
- #25 (WebApi project)
EOF
)" \
    "web-api,medium-priority" \
    "$M3"

echo ""
echo "=== M4: Translation Web App (Frontend) ==="

create_issue \
    "[M4] Create frontend project (Blazor WASM or React)" \
    "$(cat <<'EOF'
## Description
Set up the frontend project for translation management UI.

## Decision Required
**Option A: Blazor WebAssembly**
- Pro: Same C# stack, shared models from Domain, no JS build pipeline
- Con: Larger initial download, less mature UI ecosystem

**Option B: React + Vite + TypeScript**
- Pro: Rich UI ecosystem, smaller bundle, better dev experience
- Con: Separate language, need API client generation, separate build

**Recommendation:** Blazor WASM for team simplicity. React if UI complexity demands it.

## Tasks
- [ ] Make tech decision
- [ ] Create project: `src/LotroKoniecDev.WebApp/` (Blazor) or `src/web-app/` (React)
- [ ] Add to solution
- [ ] Configure dev proxy to Web API
- [ ] Set up basic routing (Home, Translations, Import, Dashboard)
- [ ] Add CSS framework (MudBlazor for Blazor, or Tailwind/shadcn for React)

## Acceptance Criteria
- Frontend project builds and serves
- Can navigate between views
- Makes API calls to backend

## Depends On
- M3 (Web API)
EOF
)" \
    "frontend,high-priority" \
    "$M4"

create_issue \
    "[M4] Create API client / HTTP service layer" \
    "$(cat <<'EOF'
## Description
HTTP client service for communicating with the Web API from frontend.

## Tasks
- [ ] Create API client (HttpClient wrapper for Blazor, or axios/fetch for React)
- [ ] Type-safe DTOs matching API responses
- [ ] Methods: `getTranslations()`, `createTranslation()`, `updateTranslation()`, etc.
- [ ] Error handling: map API errors to user-friendly messages
- [ ] Loading states management
- [ ] If Blazor: create `Services/TranslationApiClient.cs`
- [ ] If React: create `src/api/translationApi.ts`

## Acceptance Criteria
- All API endpoints have client methods
- Errors are caught and displayed to user
- Loading states tracked

## Depends On
- #36 (Frontend project)
EOF
)" \
    "frontend,high-priority" \
    "$M4"

create_issue \
    "[M4] Create Translation List view" \
    "$(cat <<'EOF'
## Description
Main translation management view — searchable, filterable, sortable table.

## Features
- Paginated table: FileId, GossipId, Polish text (truncated), Approved status, Last edited
- Search bar (searches EN and PL content)
- Filters: All / Approved / Pending / Untranslated
- Sort by: FileId, Last edited, Quest title
- Click row → open editor
- Bulk select + approve/reject

## Tasks
- [ ] Create TranslationList component/page
- [ ] Implement search with debounce (300ms)
- [ ] Implement filter buttons/tabs
- [ ] Implement pagination controls
- [ ] Implement row click → navigate to editor
- [ ] Show translation progress indicator (X of Y translated)
- [ ] Color coding: green=approved, yellow=pending, gray=untranslated

## Acceptance Criteria
- Handles 50k+ entries with smooth pagination
- Search results appear within 500ms
- Visual distinction between translation statuses
EOF
)" \
    "frontend,high-priority" \
    "$M4"

create_issue \
    "[M4] Create Translation Editor (side-by-side EN/PL)" \
    "$(cat <<'EOF'
## Description
Core editing experience — side-by-side English reference and Polish translation.

## Layout
```
┌─────────────────────┬─────────────────────┐
│  English (readonly)  │  Polish (editable)   │
│                     │                      │
│  Welcome to         │  Witaj w             │
│  Middle-earth!      │  Śródziemiu!         │
│                     │                      │
│  <DO_NOT_TOUCH>     │  <DO_NOT_TOUCH>      │
│  highlighted        │  highlighted         │
└─────────────────────┴─────────────────────┘
┌─────────────────────────────────────────────┐
│ FileId: 620756992  GossipId: 1001           │
│ Args Order: [1] [2] [3]  (drag to reorder)  │
│ ☑ Approved    Notes: [________________]     │
│ [Save] [Next ▶] [Previous ◀]               │
└─────────────────────────────────────────────┘
```

## Tasks
- [ ] Create TranslationEditor component/page
- [ ] Side-by-side layout (responsive: stacked on mobile)
- [ ] English text display (readonly, from ExportedTexts)
- [ ] Polish text editor (textarea or rich editor)
- [ ] Syntax highlight `<--DO_NOT_TOUCH!-->` placeholders (colored, non-editable)
- [ ] Args order editor (drag-and-drop or input)
- [ ] Approve checkbox
- [ ] Notes field
- [ ] Save button (auto-save option?)
- [ ] Next/Previous navigation (within current filter/search)
- [ ] Keyboard shortcuts: Ctrl+S save, Ctrl+Enter save & next

## Acceptance Criteria
- Can edit and save translations
- Placeholders visually distinct and protected
- Navigation between entries is seamless
EOF
)" \
    "frontend,high-priority" \
    "$M4"

create_issue \
    "[M4] Create Quest Browser view" \
    "$(cat <<'EOF'
## Description
Browse translations organized by quest. Search by quest title, see all strings for a quest.

## Features
- Search bar for quest titles
- Quest list with translation progress per quest
- Click quest → show all strings (description, objectives, dialog, etc.)
- Edit inline or navigate to full editor

## Tasks
- [ ] Create QuestBrowser component/page
- [ ] Group exported texts by quest (heuristic: same FileId range or title extraction)
- [ ] Search by quest title
- [ ] Show per-quest progress (translated/total)
- [ ] Expandable quest → list all strings
- [ ] Link each string to Translation Editor

## Acceptance Criteria
- Can find quest by English title
- See all strings belonging to a quest
- Navigate to editor for any string

## Depends On
- #37 (API client), #30 (Search API)
EOF
)" \
    "frontend,medium-priority" \
    "$M4"

create_issue \
    "[M4] Create Dashboard view (progress stats)" \
    "$(cat <<'EOF'
## Description
Overview dashboard showing translation progress and recent activity.

## Features
- Progress bar: X% translated (with numbers)
- Progress bar: X% approved
- Pie chart: translated / untranslated / pending approval
- Recent edits list (last 20)
- Quick actions: Import exported.txt, Export polish.txt

## Tasks
- [ ] Create Dashboard component/page
- [ ] Fetch stats from /api/stats
- [ ] Render progress bars with percentages
- [ ] Render recent edits list
- [ ] Add quick action buttons
- [ ] Auto-refresh stats periodically (or on navigation)

## Acceptance Criteria
- Dashboard loads within 1 second
- Stats accurate to last minute
- Visual progress indicators

## Depends On
- #37 (API client), #33 (Stats API)
EOF
)" \
    "frontend,medium-priority" \
    "$M4"

create_issue \
    "[M4] Create Import/Export page" \
    "$(cat <<'EOF'
## Description
Page for file upload (import) and download (export) operations.

## Features
- Upload area: drag & drop or file picker for exported.txt
- Upload area: drag & drop for polish.txt (legacy import)
- Download button: polish.txt (approved only)
- Download button: polish.txt (all)
- Import progress indicator
- Import summary (new, updated, errors)

## Tasks
- [ ] Create ImportExport component/page
- [ ] File upload with drag & drop
- [ ] Progress indicator during import
- [ ] Summary display after import
- [ ] Download buttons calling export API
- [ ] Confirmation dialog before import (will update existing data)

## Acceptance Criteria
- Can upload 10MB+ exported.txt
- Import shows progress and summary
- Download produces valid polish.txt

## Depends On
- #37 (API client)
EOF
)" \
    "frontend,medium-priority" \
    "$M4"

create_issue \
    "[M4] Syntax highlighting for DO_NOT_TOUCH placeholders" \
    "$(cat <<'EOF'
## Description
In the translation editor, `<--DO_NOT_TOUCH!-->` placeholders must be visually distinct and protected.

## Tasks
- [ ] Detect `<--DO_NOT_TOUCH!-->` in both English and Polish text
- [ ] Render as colored badges/chips (e.g., orange background, monospace)
- [ ] Display as `{arg1}`, `{arg2}` etc. for readability
- [ ] Prevent accidental deletion (warn if placeholder count changes)
- [ ] Show tooltip explaining what each placeholder represents
- [ ] Validate: Polish text must have same number of placeholders as English

## Acceptance Criteria
- Placeholders are visually distinct in editor
- Warning if translator changes placeholder count
- Readable representation instead of raw syntax

## Depends On
- #39 (Translation Editor)
EOF
)" \
    "frontend,medium-priority" \
    "$M4"

create_issue \
    "[M4] Add keyboard shortcuts for translation workflow" \
    "$(cat <<'EOF'
## Description
Keyboard shortcuts for efficient translation work.

## Shortcuts
- `Ctrl+S` — Save current translation
- `Ctrl+Enter` — Save and go to next
- `Ctrl+Shift+Enter` — Save, approve, and go to next
- `Alt+←` / `Alt+→` — Previous / Next translation
- `Ctrl+F` — Focus search
- `Ctrl+A` in list — Select all visible
- `Escape` — Back to list

## Tasks
- [ ] Implement keyboard shortcut system
- [ ] Add shortcuts to editor
- [ ] Add shortcuts to list view
- [ ] Show shortcut hints in UI (tooltips or footer)
- [ ] Ensure shortcuts don't conflict with browser defaults

## Acceptance Criteria
- All listed shortcuts work
- Shortcuts discoverable in UI

## Depends On
- #39 (Translation Editor)
EOF
)" \
    "frontend,low-priority" \
    "$M4"

create_issue \
    "[M4] Add bulk operations (approve/reject multiple)" \
    "$(cat <<'EOF'
## Description
Select multiple translations in list view and apply bulk actions.

## Tasks
- [ ] Add checkboxes to translation list rows
- [ ] Select all / deselect all
- [ ] Bulk actions toolbar: Approve selected, Reject selected, Delete selected
- [ ] Confirmation dialog for destructive actions
- [ ] Progress indicator for bulk operations
- [ ] Backend: add batch endpoints or loop through individual commands

## Acceptance Criteria
- Can select and approve 100+ translations at once
- Confirmation before destructive actions
- Progress feedback during operation

## Depends On
- #38 (Translation List)
EOF
)" \
    "frontend,low-priority" \
    "$M4"

create_issue \
    "[M4] Responsive design / mobile support" \
    "$(cat <<'EOF'
## Description
Ensure web app works on tablets and phones (for reviewing translations on the go).

## Tasks
- [ ] Editor: stack EN/PL vertically on small screens
- [ ] List: responsive table (cards on mobile)
- [ ] Navigation: hamburger menu on mobile
- [ ] Touch-friendly buttons and controls
- [ ] Test on common screen sizes

## Acceptance Criteria
- Usable on 768px+ width (tablet)
- Basic functionality on 375px+ width (phone)
EOF
)" \
    "frontend,low-priority" \
    "$M4"

echo ""
echo "=== M5: Workflow Automation ==="

create_issue \
    "[M5] Add CLI command: sync (fetch translations from API)" \
    "$(cat <<'EOF'
## Description
New CLI command that downloads polish.txt from the Web API.

## Usage
```
LotroKoniecDev sync [api_url] [output_path]
# Default: http://localhost:5000/api/export/polish.txt → translations/polish.txt
```

## Tasks
- [ ] Create `SyncTranslationsCommand` (MediatR) + Handler
- [ ] Add HttpClient for API communication
- [ ] Download polish.txt from API endpoint
- [ ] Save to translations/ directory
- [ ] Add to CLI command routing in Program.cs
- [ ] Error handling: API unreachable, timeout, etc.

## Acceptance Criteria
- `LotroKoniecDev sync` downloads latest translations
- Works with default localhost and custom URL
- Clear error messages if API unavailable

## Depends On
- M3 (Web API with export endpoint)
EOF
)" \
    "automation,medium-priority" \
    "$M5"

create_issue \
    "[M5] Create BAT files for workflow automation" \
    "$(cat <<'EOF'
## Description
Batch scripts for common workflows.

## Files
```
scripts/
  export.bat          Export English texts from DAT → data/exported.txt
  import.bat          Upload exported.txt to Web API
  sync.bat            Download polish.txt from Web API
  patch.bat           Apply translations to DAT
  run.bat             Launch LOTRO
  full-workflow.bat   sync → patch → run
  dev-start.bat       Start Web API + open browser
```

## Tasks
- [ ] Create scripts/ directory
- [ ] Create each BAT file with proper error handling
- [ ] Add pause on error (so user sees what went wrong)
- [ ] Add color output (ANSI escape codes)
- [ ] full-workflow.bat chains: sync → patch → run
- [ ] dev-start.bat: dotnet run WebApi + start browser
- [ ] Add README for scripts

## Acceptance Criteria
- Double-click any .bat to execute workflow
- Errors are visible and clear
- full-workflow.bat does everything in one click

## Depends On
- #47 (CLI sync command)
EOF
)" \
    "automation,medium-priority" \
    "$M5"

create_issue \
    "[M5] Add CLI command: import (push exported.txt to API)" \
    "$(cat <<'EOF'
## Description
CLI command to upload exported.txt to the Web API (import English reference texts).

## Usage
```
LotroKoniecDev import [exported.txt] [api_url]
```

## Tasks
- [ ] Create `ImportToApiCommand` (MediatR) + Handler
- [ ] Read exported.txt, POST to /api/import/exported-texts
- [ ] Show upload progress
- [ ] Display import summary from API response

## Acceptance Criteria
- Uploads exported.txt to running Web API
- Shows summary (new/updated/unchanged)

## Depends On
- M3 (Web API with import endpoint)
EOF
)" \
    "automation,medium-priority" \
    "$M5"

create_issue \
    "[M5] Update README with new workflow documentation" \
    "$(cat <<'EOF'
## Description
Update project documentation to reflect the new architecture and workflow.

## Tasks
- [ ] Update README.md with:
  - New architecture diagram (CLI + Web API + Web App)
  - Updated build & run instructions
  - Translation workflow guide (export → web app → patch → play)
  - BAT files documentation
  - API endpoint reference
- [ ] Update CLAUDE.md with new project structure
- [ ] Add CONTRIBUTING.md (for potential translators)
- [ ] Add screenshots of Web App

## Acceptance Criteria
- New contributor can understand and set up the project from README
- Workflow is clearly documented

## Depends On
- M4 (Web App)
EOF
)" \
    "automation,low-priority" \
    "$M5"

create_issue \
    "[M5] Docker Compose for dev environment" \
    "$(cat <<'EOF'
## Description
Docker Compose setup for running Web API + frontend locally.

## Tasks
- [ ] Create `Dockerfile` for Web API
- [ ] Create `Dockerfile` for Web App (if separate)
- [ ] Create `docker-compose.yml`:
  - Web API on port 5000
  - Web App on port 3000 (or served by API)
  - SQLite volume mount
- [ ] Add `.dockerignore`
- [ ] Add `scripts/dev-up.bat` / `dev-up.sh`

## Acceptance Criteria
- `docker compose up` starts everything
- Data persisted in volume
- Works on fresh machine with only Docker installed

## Depends On
- M3 (Web API)
EOF
)" \
    "automation,low-priority" \
    "$M5"

create_issue \
    "[M5] Create setup/first-run script" \
    "$(cat <<'EOF'
## Description
First-run script that sets up the database and imports initial data.

## Tasks
- [ ] Create `scripts/setup.bat` / `setup.sh`
- [ ] Run EF Core migrations (create database)
- [ ] Import existing polish.txt (legacy translations) if found
- [ ] Import exported.txt if found
- [ ] Print setup summary and next steps
- [ ] Handle "already set up" case gracefully

## Acceptance Criteria
- New user runs setup once, everything is ready
- Can re-run safely (idempotent)

## Depends On
- M2 (Database), M3 (Web API)
EOF
)" \
    "automation,medium-priority" \
    "$M5"

echo ""
echo "=== DONE ==="
echo "Created all issues. Check: gh issue list --repo $REPO --limit 60"
