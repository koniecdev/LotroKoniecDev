# The TMS Handbook — LOTRO Polish Translation Platform

> **One document to understand the whole system.** Written in simple English, from the bottom
> (the problem and the domain) to the top (deployment and operations). Use it to onboard a new
> team member in a day, or to prepare for an interview where you must explain and defend the
> design.
>
> When this handbook and the code disagree, the code wins: fix the handbook in the same pull
> request. Business rules come from the wiki (`CLAUDE.md` → "Source of truth").

---

## How to read this document

The handbook goes **bottom to top**. Each part builds on the one before it:

| Part | What you learn |
|---|---|
| 1. The problem | Why this project exists at all |
| 2. The domain | The business concepts, in plain words — no code yet |
| 3. Building blocks | The small code patterns everything else is made of |
| 4. The system map | The projects, and how they connect |
| 5. The main flows | End-to-end walks through every important scenario |
| 6. The database | Tables, indexes, concurrency, migrations |
| 7. Auth | How login and permissions really work |
| 8. The frontend | The Blazor web app |
| 9. Testing | What we test and how |
| 10. Run it locally | Get the whole system working on your machine |
| 11. CI/CD and production | Pipelines, the servers, money |
| 12. Defend the design | Interview questions and strong answers |
| 13. Glossary | Every term, A to Z |
| Appendix A | A map of the architecture decisions (ADRs), grouped by theme |
| Appendix B | A map of the feature specifications (specs) |

**Shortcuts for different readers:**

- *New developer, first day:* Parts 1–5, then Part 10 to run it.
- *Interview preparation:* Parts 1–2 for the story, Part 12 for the answers, Appendix A for the decisions.
- *Operations / deployment:* Parts 6, 10, 11, plus `docs/deployment/runbook.md`.

**Where to go deeper.** This handbook is the entry point. The repository also has:
[DOMAIN.md](DOMAIN.md) (domain deep dive, Polish), [API.md](API.md) (full endpoint reference),
[INVARIANTS.md](INVARIANTS.md) (every business rule with its file), [auth-tutorial.md](auth-tutorial.md)
(auth from zero, Polish), `docs/knowledge-base/` (results of real experiments with the game),
`docs/deployment/runbook.md` (operations manual), and `docs/adr/` + `docs/specs/` (the original
decision and specification documents).

---

## Part 1 — The problem we solve

### 1.1 The short story

**The Lord of the Rings Online (LOTRO)** is an online game. It has no official Polish
translation. This project builds one — and, more importantly, builds the **machine** that lets a
team of volunteers translate about **790,000 text fragments** and keep them correct while the
game keeps changing.

The game stores all its texts inside one very large binary file called the **DAT file**
(`client_local_English.dat`). You cannot edit it with a text editor. You need special tooling.

The project has two big pieces:

1. **The Patcher** — a Windows command-line tool (CLI). It can:
   - `export` — read all English texts out of the DAT file into a plain text file,
   - `patch` — write Polish translations back into the DAT file,
   - `launch` — download the newest translations, patch if needed, and start the game.
2. **The TMS (Translation Management System)** — a web platform where translators work
   together. It imports the exported English texts, gives translators an editor with search and
   a review workflow, and publishes a single "official" translation file that every player's
   patcher downloads.

### 1.2 Why two separate systems?

The patcher and the TMS live in one repository but are **two separate worlds** (in architecture
language: two *bounded contexts* — see ADR-0002):

- The **patcher** must run on **Windows**, in **32-bit x86** mode, because it calls a native
  library (`datexport.dll`) that reads and writes the DAT file. It runs on a player's gaming PC.
- The **TMS** is a **web system**: PostgreSQL database, two APIs, and a web frontend. It runs in
  **Linux containers** on a server.

These two worlds **never share code**. They cannot: one needs Windows-native binaries, the other
runs on Linux. Instead, they share one **data contract**: a simple text-file format (the `||`
file, Part 2.5). The patcher writes that format and the TMS reads it; the TMS writes it back and
the patcher reads it. Each side has its **own parser**, and "parity tests" make sure the two
parsers always agree.

```
   WINDOWS GAMING PC                              LINUX SERVER
 ┌───────────────────────┐                     ┌──────────────────────────────┐
 │  Patcher CLI          │   exported.txt      │  TMS                         │
 │  (reads/writes the    │ ──────────────────► │  auth-api + tms-api          │
 │   game's DAT file)    │                     │  + Blazor frontend           │
 │                       │   polish txt file   │  + PostgreSQL                │
 │                       │ ◄────────────────── │  (HTTP download, cached)     │
 └───────────────────────┘                     └──────────────────────────────┘
        no shared code — only a shared text-file format
```

### 1.3 Where the project stands today

- The **patcher is finished and proven**. Nine live tests against real game updates confirmed
  that it works and — an important discovery — that **translations survive game updates**
  (details in Part 12, question 17).
- The **TMS backend is built**: import, editor endpoints, approval, file distribution, auth.
- The **frontend is built**: home page, translation list, side-by-side editor, dashboard,
  game-version management, import/export page, "Moje konto" and the terms of service.
- The system **deploys automatically to Hetzner servers** (staging first, then production after a
  human approves). The public site is `https://lotro-translator.pl`. Hosting moved off Azure in
  2026-07 (ADR-0034).
- There are **no production users yet**. Breaking changes are still cheap, and several cost/risk
  decisions (Part 11) are made for a zero-user world on purpose.

### 1.4 One inspiration and one starting point

- A **Russian community** translated LOTRO years ago with the same core technique (same native
  DLL, same fragment identity). Their experience (`docs/RUSSIAN_PROJECT_RESEARCH.md`) confirmed the
  approach and warned about over-engineering.
- The TMS architecture **started as a copy of a proven earlier project** by the same author:
  vertical slices, domain-driven design, the Result pattern, the self-hosted auth server, testing
  style. One rule changed repo-wide: **no mediator library** (ADR-0001). The copy is finished and
  the two code bases have gone their own ways, so this repo's own code is now the only reference
  (ADR-0002 §4, amended 2026-10-11).

---

## Part 2 — The domain, in plain words

Read this part slowly. Everything else in the system exists to serve these ideas. The business
rules themselves are written in the wiki (`koniecdev/LotroKoniecDev.wiki`), which outranks this
handbook.

### 2.1 The core vocabulary

| Term | Plain meaning |
|---|---|
| **Fragment** | One piece of game text. Example: one quest dialog line. The game has ~790,000 of them. |
| **FileId + GossipId** | The fragment's identity. `FileId` says which internal game file holds it; `GossipId` is the fragment's number inside that file. Together they form the **FragmentKey**. This pair is **stable across game updates** — proven by real experiments. The text may change; the key does not. |
| **Source text** | The English text of a fragment, plus its argument metadata. This is what translators translate *from*. |
| **Translation** | Our Polish text for one fragment. In the database: **one row per fragment, forever** — the row is updated in place, never copied per version. |
| **Placeholder** | The marker `<--DO_NOT_TOUCH!-->` inside a text. The game inserts a value there at runtime (a player name, a number). Translators must keep every placeholder. |
| **Args order** | Polish grammar sometimes needs the inserted values in a different order than English. This field (for example `2-1`) tells the patcher how to reorder them. |
| **Game version** | One release of the game, named the way LOTRO names them: `48`, `48.7`, `47.1.1`. Every import of texts is tied to a game version. |
| **Import** | The admin uploads a fresh `exported.txt` (all English texts) for a new game version. The system compares it with what it has (a **diff**) and updates the catalog. |
| **Invalidation** | When a fragment's English changes, our existing Polish for it becomes suspect. The system marks it **NeedsReview**. That is invalidation — nothing is deleted. |
| **Approval** | An admin reviews a translation and marks it **Approved**. Only approved translations reach players. |
| **The artifact** (distributed file) | One pre-built text file with all approved translations. Players' patchers download it. It is rebuilt in the background after changes. |
| **Translator** | A person with an account. The TMS keeps a small local profile (display name, email) so lists can show *who* translated *what*. |

### 2.2 The life of one translation (the state machine)

Every translation row is always in exactly **one** of four states:

```
                  ProvideTranslation              Approve
  Untranslated ───────────────────► Draft ───────────────────► Approved
                                    ▲   │                       ▲    │
               ProvideTranslation   │   │ English       Approve │    │ English
               (translator fixes)   │   │ changes               │    │ changes
                                    │   ▼                       │    ▼
                                ┌──────────────────────────────────────┐
                                │      NeedsReview (invalidated)       │
                                └──────────────────────────────────────┘
```

- **Untranslated** — the fragment exists, no Polish yet.
- **Draft** — someone wrote Polish, but it is not reviewed yet. *Editing an Approved row also
  moves it back to Draft* — an edit un-publishes the row until someone approves it again.
- **Approved** — reviewed and published. Only these rows go into the distributed file.
- **NeedsReview** — the English changed after the Polish was written. A human must re-check. A
  reviewer may approve it as it is ("the old Polish still fits") or a translator may fix it first.

Three important design choices hide in this picture:

1. **One status field, no extra flags.** There is no separate "invalidated" boolean. If there
   were, you could have an impossible combination like "Approved and invalidated at the same
   time". With one enum, illegal states cannot be represented.
2. **Invalidation keeps the old work.** The Polish stays in the row as a starting point, and the
   *old English* is saved in `PreviousSourceText`. The editor then shows: old English → new
   English → current Polish, side by side. `PreviousSourceText` is **frozen at the first
   invalidation** — if the English changes three more times before a human looks, the field
   still holds the English the current Polish was written against. Approving clears it.
3. **"Fallback to English" by exclusion.** An invalidated row is **left out** of the distributed
   file. We never copy English into the Polish field. The player's game shows the fresh English
   text, because the patcher never touches that fragment.

There is also a separate, reversible flag: **soft removal**. When a game update removes a
fragment, we do not delete the row — we stamp `RemovedInVersion`. If a later update brings the
fragment back with the same text, we clear the stamp (`Restore`) and the row keeps its old
status — even Approved. History is never thrown away.

### 2.3 The life of one game version

A `GameVersion` also has a small state machine:

```
  Unprocessed ──(admin imports the export file)──►  Processed
       │
       └──(a newer version gets processed first)──►  Superseded
```

- **Unprocessed** — we know the version exists (someone registered it), but no text import
  happened for it yet.
- **Processed** — an import ran against it. Importing again into it is allowed and is
  **idempotent** (running it twice has the same effect as running it once).
- **Superseded** — a *newer* version was processed while this one was still waiting, so this one
  will never be processed. Example: versions 48.1 and 48.2 both wait; the admin imports 48.2;
  48.1 becomes Superseded automatically.

Forbidden moves are enforced in code: a Processed version never becomes Superseded, and a
Superseded version is never processed. A Processed version is never deleted, because an import
ran against it. An Unprocessed or Superseded version that no translation points to may be deleted,
which frees its version number for the real update (#624).

Game version names are **canonical**: `48`, `48.0` and `48.0.0` are the same version. The system
removes meaningless trailing zeros before storing or comparing (ADR-0003). A version has one to
three segments. Without the canonical form, duplicate checks would silently fail.

**How do new versions appear?** An admin registers them by hand on the `/game-versions` page. The
version comes from the official release notes, because the DAT file's internal version number
never changes (Part 12, question 17). An automatic "forum watcher" is cut to post-MVP, and
producing the export stays a manual step too (ADR-0030).

### 2.4 What happens on a game update (the heart of the domain)

This is the scenario the whole domain model was designed around:

1. LOTRO releases update `48.7`. Some English texts are new, some are reworded, some are gone.
2. An admin registers game version `48.7` in the TMS (status: Unprocessed).
3. On a Windows machine, the patcher runs `export` → a fresh `exported.txt` (~80 MB, ~790k rows).
4. The admin uploads that file to the TMS for version `48.7`.
5. The TMS **diffs** the file against its catalog. Every row lands in exactly one of five
   outcomes:
   - **Added** — new fragment → insert a new row as Untranslated.
   - **Source changed** — same key, different English → overwrite the English; if the row had
     Polish (Draft or Approved), invalidate it to NeedsReview and freeze `PreviousSourceText`.
   - **Removed** — the key is not in the new file → soft-remove the row.
   - **Restored** — the key is back and the English is identical → clear the removal stamp,
     keep the old status (even Approved).
   - **Unchanged** — nothing to do. The row is not written and its `UpdatedAt` does not move.

   One special case counts as unchanged: the **echo**. The admin exports from their own patched
   game, so a translated row comes back with our Polish as its "English". The diff recognizes our
   own text and does not invalidate the row (spec 0012).
6. The version becomes Processed; older waiting versions become Superseded.
7. The distributed file is rebuilt in the background. Invalidated rows are excluded, so no
   player ever sees Polish that no longer matches the game.
8. Translators see the NeedsReview queue and work through it; admins approve; the file grows
   again.

Two safety rules guard step 5:

- **All-or-nothing parsing.** If even one line of the upload cannot be parsed, the whole import
  is rejected. A silently skipped line looks exactly like a removed fragment, and "removed" has
  consequences. A cut-off or corrupted file must fail loudly.
- **The mass-removal guard.** If the diff wants to remove more than **20%** of the active
  fragments, the import is rejected unless the admin passes an override flag. A half-file upload
  would otherwise soft-remove half the catalog. On the first import into an empty catalog the
  removed share is 0%, so the guard never fires there.

### 2.5 The `||` file — the contract between the two worlds

Both directions of data travel use one plain-text format, one line per fragment:

```
# comment lines start with a hash; empty lines are ignored
file_id||gossip_id||content||args_order||args_id||approved||source_digest
620756992||1001||Witaj w Śródziemiu!||NULL||NULL||1||3f9a1c0e7b2d4a55
620756992||1002||Masz <--DO_NOT_TOUCH!--> złota i <--DO_NOT_TOUCH!--> srebra.||2-1||2-1||1||9c02e4d1a7f0b366
```

The format, its columns and worked examples live in one place:
[README → Translation file format](../README.md#translation-file-format-the-inter-context-contract).
The rules that matter most, each with the ADR that holds the reasoning:

- **`source_digest` says which English the row belongs to (ADR-0047).** The patcher writes a
  fragment only when the DAT still holds that English, or what the patcher itself last wrote
  there. So between a game update and the next import, a translation made for the old wording is
  skipped, and the player sees the game's own new text. A six-column file is not patchable.
- **The content may contain `||`.** Both parsers *carve* the line from both ends instead of
  splitting it (ADR-0042).
- **Content is escaped in the file and raw everywhere else (ADR-0039).** A line break becomes
  `\r` / `\n` and a backslash becomes `\\`. Every writer escapes and every reader unescapes.
- **Content is at most 32,767 UTF-16 code units**, the most the DAT can hold (ADR-0043).
- **`approved`**: the patcher patches only rows with `1`. The TMS ignores the column on import
  (approval belongs to the review workflow) and always writes `1` (it exports only approved rows).
- The TMS writes **CRLF** line endings and sorts rows by `(FileId, GossipId)`, so the same data
  always gives byte-identical output and a stable content hash (the HTTP ETag, Part 5.7).

**Changing this format requires an ADR plus updated "golden" test files on both sides.** It is
protected by process and by tests (`ParserContractParityTests`, `TranslationFileSerializerParityTests`
— the TMS output must survive a round trip through the *patcher's* parser byte-identically).

### 2.6 People and permissions

Two roles exist (defined once, in `SharedKernel/Authorization/AuthConstants.cs`):

- **Translator** — can browse everything, use the editor, and save drafts (`upsert`). Every new
  self-registered account gets this role.
- **Admin** — everything a Translator can, plus: approve translations (single and bulk), import
  export files, register and delete game versions. The admin account is seeded at startup.

A few endpoints are public on purpose: the **translation list** (read-only browsing), the public
**progress** numbers, the **translation-file download** (players' patchers have no accounts) and
the API's **discovery document**. Everything else needs a login by default — an endpoint must
*opt out* to be public, never the other way round.

---

## Part 3 — The building blocks of the code

Now we go to the bottom of the code and climb up. Every pattern here answers one question:
**how do we make business rules impossible to break by accident?**

All the pieces below live in `src/SharedKernel/LotroKoniecDev.SharedKernel` (shared bricks) and
`src/TranslationSystem/LotroKoniecDev.TranslationSystem.{Primitives,Domain}` (the TMS domain).

### 3.1 Errors are values: `Result` and `Error`

**The rule:** a business failure is a *normal answer*, not an emergency. "You cannot approve a
translation that has no Polish text" is not a crash — it is information. So domain and
application code never throws exceptions for business rules. It returns a `Result`:

```csharp
Result r1 = Result.Success();
Result<GameVersion> r2 = Result.Failure<GameVersion>(
    DomainErrors.GameVersionEntity.VersionAlreadyRegistered(version));

if (r2.IsFailure)
{
    // r2.Error has a Code, a Message, and a Type
}
```

An `Error` carries three things: a machine-readable **Code** (like
`TranslationEntity.CannotApproveWithoutTranslation`), a human **Message**, and a **Type** that
the API layer maps to an HTTP status:

| Error type | Meaning | HTTP status |
|---|---|---|
| `Validation` | The input is wrong | 400 |
| `NotFound` | The thing does not exist | 404 |
| `Forbidden` | You may not do this | 403 |
| `DataConflict` | The action conflicts with current state | 422 |
| `TooManyRequests` | A budget is spent | 429 |
| `Failure` | Everything else | 500 |

The `Result` type protects itself: a success cannot carry an error, a failure must carry one,
and reading `.Value` of a failed result throws at once (that would be a programming bug).

**Exceptions still exist — for programmer errors only.** Passing `null` where null is
impossible, an empty ID, an unset enum — these throw at once (via `Ensure.*` guards or
`ArgumentNullException.ThrowIfNull`). The line is simple: *a user or the data can cause a
`Result.Failure`; only a developer mistake can cause an exception.* The API's exception handlers
are a safety net, not a control-flow mechanism.

There is also `Maybe<T>`: a container that either holds a value or is empty. Repositories return
`Maybe<Translation>` instead of `null` for "not found", so every caller must handle the missing
case.

### 3.2 Strongly-typed IDs

Every entity ID is its own small type instead of a raw `Guid`:

```csharp
public readonly record struct TranslationId : IStronglyTypedId<TranslationId>
{
    public Guid Value { get; }
    public static TranslationId Create();            // new, time-ordered GUID (version 7)
    public static TranslationId Create(Guid id);     // from untrusted input — validated
    public static TranslationId FromValue(Guid id);  // from the database — trusted, no checks
}
```

Why bother?

- **You cannot mix IDs up.** Passing a `GameVersionId` where a `TranslationId` is expected is a
  *compile error*, not a production bug at 2 a.m.
- **GUID version 7 is time-ordered**, so new rows land near each other in the database index —
  better insert performance than random GUIDs.
- The **two factory methods are a security idea**: input from outside goes through
  `Create(Guid)` (an empty GUID is rejected), while values read back from our own database go
  through `FromValue` (the store is trusted).

These are **hand-written**, about 30 lines each (ADR-0010): the source-generator package used at
first was unmaintained, and its generated public constructor skipped validation.

One special ID lives in SharedKernel: **`IdentityId`** — the ID of a user account in the auth
system. It is the *only* value that crosses the boundary between the auth world and the TMS
world (as the `sub` claim inside a token).

### 3.3 Value objects — no "primitive obsession"

**The rule:** if a concept has a constraint, it gets its own type. A game version is not a
`string`; it is a `LotroNotationVersion`. The constraint lives *inside* the type, in one place,
and an invalid instance **cannot exist**.

```csharp
Result<LotroNotationVersion> v = LotroNotationVersion.Create("48.0");
// v.Value.Value == "48"  ← canonical form: trailing zeros removed
```

The important value objects (VOs):

| Value object | Wraps | Enforces |
|---|---|---|
| `LotroNotationVersion` | version string | max 12 chars, one to three segments, digits-and-dots grammar, canonical form (`48.0` → `48`) |
| `FragmentKey` | FileId + GossipId | `FileId > 0`, `GossipId >= 0` (zero is legal — the patcher parser allows it, so the TMS must too) |
| `TranslationSource` | English text + ArgsOrder + ArgsId | turns `"NULL"`/blank args into real `null`; **equality covers all three fields**, so a change in argument structure counts as a change of meaning even if the text is identical |
| `DisplayName` | translator's name | trimmed, non-empty, max 150 |
| `Email` | translator's email | trimmed, simple format check, max 250 |

Two details worth quoting in an interview:

- **Validation order is fixed**: empty check → trim → length → format. So an all-spaces input
  fails as "empty", never as "wrong format" — the same error codes everywhere.
- Value objects compare **by content** (the base class compares a list of "atomic values"), not
  by reference. Two `FragmentKey(1, 5)` instances are equal. Entities compare by ID.

### 3.4 Entities and aggregates

- An **entity** has an identity (an ID) and a life story. Two entities are the same thing if
  their IDs match, even when their data differs.
- An **aggregate** is a small cluster of data with **one guard at the door** (the *aggregate
  root*). All changes go through methods on the root, and those methods enforce the rules. There
  are no public setters.

The TMS has exactly **three aggregates**, all small:

1. **`GameVersion`** — the version state machine from Part 2.3. Methods: `MarkAsProcessed()`,
   `MarkSuperseded()`, `EnsureCanBeDeleted()` — each returns `Result` and refuses illegal moves.
2. **`Translation`** — the rich one; the state machine from Part 2.2. Methods:
   `CreateUntranslated(...)` (factory), `ApplySourceChange(...)`, `MarkRemoved(...)`,
   `Restore(...)`, `ProvideTranslation(...)`, `Approve(...)`. `Approve` carries the real guards
   ("no Polish text → fail", "removed → fail") and returns `Result`.
3. **`Translator`** — a lean local profile of an auth user: `IdentityId`, `DisplayName`,
   `Email?`, plus `RefreshProfile(...)` so a renamed account catches up, and `Erase()` for an
   account the auth system deleted (ADR-0065).

Rules that keep the model clean:

- **Aggregates reference each other only by ID.** A `Translation` stores a `GameVersionId` and
  `TranslatorId?`s — never an object reference to another aggregate.
- **Not everything is an aggregate.** The pre-built distribution file
  (`PrecomputedTranslationFile`) guards no business rule — it is derived data, fully regenerable
  from translations. It is a plain projection with its own small store interface, *not* an
  aggregate with a repository (ADR-0007).
- Class layout follows one fixed order (constants → fields → properties → methods → static
  factory → constructors → private helpers — the order in `CLAUDE.md`), so every file reads the
  same way.

### 3.5 Repositories and the Unit of Work

A **repository** is the door to stored aggregates: `GetByIdAsync` (returns `Maybe<T>`),
`ExistsAsync`, `Insert`, `Remove`, plus a few purpose-built methods per aggregate (for example
`ITranslationRepository.GetByFragmentKeyAsync`, or `AnyReferencesGameVersionAsync`, which backs
the "may I delete this version?" check).

The **Unit of Work** (`IUnitOfWork`) is the "commit button". Handlers change aggregates in
memory, then call `SaveChangesAsync` once — one transaction, all-or-nothing. The import uses two
special helpers on it (Part 5.4): `ExecuteInTransactionAsync` (a transaction that survives
transient network faults safely) and `SaveChangesAndClearAsync` (save one chunk, forget it, keep
memory flat).

### 3.6 Commands, queries and handlers — CQRS without a mediator

Every use case is either a **command** (changes state) or a **query** (reads state). Each is a
small record, handled by exactly one handler class:

```csharp
public interface ICommand<TResponse>;
public interface IQuery<TResponse>;

public interface ICommandHandler<in TCommand, TResponse> where TCommand : ICommand<TResponse>
{
    ValueTask<TResponse> Handle(TCommand command, CancellationToken cancellationToken);
}
// IQueryHandler looks the same.
```

That is the **whole messaging framework** — four interfaces, hand-written. There is **no
MediatR** and no mediator of any kind (ADR-0001). Instead of `sender.Send(command)` going through
a pipeline, the endpoint asks dependency injection for the **exact handler interface** and calls
it:

```csharp
// DI registration — one explicit line per use case:
services.AddScoped<ICommandHandler<ApproveTranslation.Command, Result>, ApproveTranslation.Handler>();

// The endpoint takes the closed interface as a parameter and just calls it:
ICommandHandler<ApproveTranslation.Command, Result> handler ...
Result result = await handler.Handle(command, cancellationToken);
```

What the mediator would have given us, we do explicitly instead:

- **Validation**: command handlers inject `IValidator<TCommand>` (FluentValidation) and turn
  failures into `Result.Failure` — never a thrown exception. Queries validate in the handler
  (house rule: validators exist *for commands only*).
- **Logging**: a plain `ILogger<Handler>` inside the handler.
- **Wiring safety**: DI validation is on in every environment, so a missing registration fails at
  startup, and "find all usages" works, because the call is a normal interface call.

Why this is better *here*: the mediator's indirection served nobody (no cross-cutting pipeline
was used), it hid the wiring until runtime, and it pulled in a vulnerable package. Full argument:
Part 12, question 2.

### 3.7 The read side: read models

The system separates **writing** from **reading** (CQRS):

- **Writes** load an aggregate through a repository, call a behavior method, save via the Unit
  of Work. The aggregate enforces the rules.
- **Reads** never touch aggregates. They query **read models** — plain records with public
  properties and no behavior — through a read-only database context
  (`IApplicationReadDbContext`, no change tracking).

Both sides map to the **same physical tables** (no copies, no synchronization): the write model
maps `Translation` with its value objects onto the `Translations` table; `TranslationReadModel`
maps the same table as flat columns, plus joins to show submitter and approver names. The read
side can shape data freely for lists and filters, and it has no methods that could break a rule.

### 3.8 The one projection: the pre-built translation file

`PrecomputedTranslationFile` (project `TranslationSystem.Projections`) is one row per language
holding the full serialized `||` file, its SHA-256 content hash, and a timestamp. It is written
through a small store port (`IPrecomputedTranslationFileStore`) with a set-based update that never
loads the old multi-megabyte content into memory. Who rebuilds it and when — Part 5.6.

### 3.9 The diff engine — a pure domain service

The import comparison (`TranslationDiffService`) is a **static, pure function**: stored catalog
in, uploaded file in, **plan** out (`TranslationDiffPlan`: which keys are added, which IDs
changed source, which are removed or restored, plus counters). It touches no database and has no
side effects, so it is easy to unit-test.

Because the catalog is ~790k rows, the service stays **small in memory**:

- The stored side streams through as compact value structs (`StoredSourceDigest`: ID, key,
  hash, echo hash, status, removed flag) — never full aggregates, never all rows at once.
- Source texts are compared by a **128-bit hash** (`SourceHash`, SHA-256 cut to 128 bits) of
  *(text, args order, args id)*, with length framing so `("ab","c")` and `("a","bc")` hash
  differently. The chance of a false "unchanged" at this scale is about 10⁻²⁶.
- The plan grows with the **diff**, not with the file: a typical update touches a few thousand
  rows out of 790k.

This design came out of a production incident (the import once ran the server out of memory) —
Part 5.4 and Part 12, question 13.

---

## Part 4 — The system map

### 4.1 The projects

```
src/
  SharedKernel/                     shared bricks: Result, Maybe, Error, Ensure,
    LotroKoniecDev.SharedKernel     strongly-typed-ID base, ICommand/IQuery interfaces,
                                    auth constants (roles, scopes, client ids)

  TranslationSystem/                                  ← the TMS core
    ...TranslationSystem.Primitives                   IDs + enums per aggregate (no logic)
    ...TranslationSystem.Domain                       aggregates, value objects, errors,
                                                      repository interfaces, diff service
    ...TranslationSystem.ReadModels                   read-side records (POCO)
    ...TranslationSystem.ReadModels.EntityFramework   EF mappings for the read records
    ...TranslationSystem.Projections                  PrecomputedTranslationFile + store port
    ...TranslationSystem.Persistence                  both DbContexts, EF configs, repositories,
                                                      migrations, bulk COPY machinery
    ...TranslationSystem.Contracts                    request/response DTOs (shared with frontend)
    ...TranslationSystem.API                          the web API: one file per use case

  AuthSystem/                       the login server (OpenIddict + ASP.NET Identity):
    ...AuthSystem.{API,Domain,      its own domain, its own database, its own Razor pages
       Infrastructure,Persistence,Contracts}

  Frontend/
    ...Frontend                     Blazor Static SSR web app (the translators' UI)

  Patcher/                          the Windows CLI (five projects, Clean Architecture):
    ...{Primitives,Domain,Application,Infrastructure,Cli}

  Utilities/                        small shared helpers (HATEOAS link types, logging redaction,
                                    options validation)
```

Dependency direction inside the TMS (arrows mean "may reference"):

```
API ──► Contracts ──► Primitives ──► SharedKernel
 │  ──► Domain ─────► Primitives, SharedKernel
 │  ──► ReadModels(+EF), Projections, Persistence
Persistence ──► Domain, ReadModels, Projections
Frontend ──► Contracts (DTOs only — never Domain, never Persistence)
```

The patcher references **nothing** from the TMS, and the TMS references **nothing** from the
patcher. The frontend talks to the API over HTTP using the `Contracts` DTOs. These rules are
checked by `Architecture.Tests.Unit` (Part 9).

### 4.2 One use case = one file (vertical slices)

The API is organized by **feature**, not by technical layer. Every use case is one file under
`TranslationSystem.API/Features/<Area>/<Action>.cs`, with three nested pieces:

```csharp
internal sealed class ApproveTranslation : IEndpoint
{
    internal sealed record Command(TranslationId Id) : ICommand<Result>;

    internal sealed class Validator : AbstractValidator<Command> { ... }

    internal sealed class Handler : ICommandHandler<Command, Result>
    {
        // explicit constructor injection: repository + unit of work + validator + ...
        public async ValueTask<Result> Handle(Command command, CancellationToken ct) { ... }
    }

    public void MapEndpoint(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/translations/{id:guid}/approve", ...)
           .RequireAuthorization(AuthorizationPolicies.RequireAdminRole);
    }
}
```

All `IEndpoint` classes are found by an assembly scan at startup and mapped automatically.
Adding a feature means: add one file, add one DI line for the handler, add DTOs to `Contracts`,
add tests. Deleting a feature means deleting one file. That is the point of vertical slices:
**change stays local**.

### 4.3 The runtime picture

Three applications and one database run together. In development they all run on your machine:

```
                 browser (translator)
                        │ HTTPS
                        ▼
   ┌–– login ––► auth-api :5003 ◄––– validates cookies, issues tokens (OpenIddict)
   │                    ▲             └─► RabbitMQ ─► sends e-mails; tells tms-api about erased accounts
   │                    │ back-channel: code→token, JWKS (public keys)
   ▼                    │
 frontend :7017 ────────┘
   (Blazor SSR)─────────► tms-api :5002 ──► PostgreSQL :5432 (2 databases:
        bearer token          ▲                lotro_translation + lotro_auth)
                              │
   patcher CLI (player) ──────┘  GET /api/v1/translation-files/polish  (anonymous, ETag-cached)
```

- **auth-api** — the identity provider. Login and register pages, token endpoint, user database.
- **tms-api** — the domain API. Validates tokens issued by auth-api (it never sees passwords).
- **frontend** — server-rendered UI. Holds the user's session cookie; calls tms-api with the
  user's access token; never talks to the database.
- **the patcher CLI** — an anonymous client: it reads the discovery document and downloads the
  file.
- **RabbitMQ** — the message broker behind auth-api's outbox: transactional e-mails and the
  "account erased" message to tms-api (ADR-0035 to ADR-0038, ADR-0065).

Other supporting services in development: **mailpit** (a fake SMTP server with a web inbox at
:8025 — catches confirmation emails) and the **Aspire dashboard** (:18888 — logs and traces).

---

## Part 5 — The main flows, end to end

This part walks through every important scenario step by step.

### 5.1 Login (OIDC authorization code + PKCE)

The system uses the standard web login flow. In plain words:

1. You click **Zaloguj** on the frontend. The frontend redirects your browser to auth-api's
   `/connect/authorize`, adding its client ID, a redirect address, and a **PKCE challenge** (a
   one-time secret hash that proves later that the same app finishes the flow — protection
   against stolen redirect codes).
2. auth-api shows its **login page**. You enter **email + password** (email is the login; the
   username is only a display name — ADR-0022).
3. auth-api checks the password (with brute-force protections — Part 7.4), then redirects your
   browser back to the frontend with a short-lived, one-time **authorization code**.
4. The frontend's server exchanges that code (plus the PKCE secret) at `/connect/token` for:
   - an **access token** — a signed JWT, valid **5 minutes**, carrying `sub` (your account ID),
     `name`, `email` and `role`. It is short on purpose: the TMS validates it locally, so this is
     also how long a revoked session keeps working (ADR-0049);
   - a **refresh token** — a *reference* token, valid **9 hours**, stored in the auth database so
     it can be revoked. It is *rolling*: every use replaces it with a new one valid for 9 hours
     again. The 9 hours are the cookie's 8 idle hours (step 5) plus a margin (#1014). It also
     carries your account's security stamp, so it stops working once that stamp changes, even if
     revoking it failed (#848). A password change or reset, an e-mail change or its undo, and
     scheduling or cancelling a deletion all change the stamp.
5. The frontend stores the tokens **inside an encrypted cookie** (`.lotrokoniecdev.auth`,
   HttpOnly, 8-hour sliding). The browser never sees raw tokens in JavaScript — there is no
   JavaScript.
6. On every later request, a cookie validator (`CookieTokenRefresher`) keeps the session healthy:
   if the access token is near expiry, it uses the refresh token to get a new one, and it ends
   sessions that an earlier refused API call marked as dead. It never checks the access token
   itself: tms-api checks every token it receives, and its refusal is what ends a session with a
   bad token (ADR-0067, #1058). So a live session that needs no renewal never calls auth-api. If
   auth-api fails only for a moment (busy, restarting, or rate-limiting a shared address), the
   session survives while its access token still works, and the next page tries again (#1062).
   When nothing can be saved, you are signed out cleanly and see a one-time "session expired"
   note.

If auth-api is down and the frontend has no cached copy of its discovery document, the login
button leads to a friendly **503 "login unavailable"** page instead of an error.

### 5.2 Lazy provisioning — how a Translator profile appears

The auth system and the TMS are separate worlds with separate databases. When you register, the
auth system does **not** call the TMS to create a profile (no cross-system transaction, nothing
to break). Instead (ADR-0004):

- On the **first authenticated request** to tms-api, a middleware (and every write handler, which
  must succeed) calls the **provisioner**: *"find a Translator row for this `sub`; if there is
  none, create it from the token's name/email claims."*
- The operation is **idempotent**: a unique database index on `IdentityId` guarantees one row
  per account even if two first requests race — the loser detaches its insert, re-reads the
  winner's row, and continues.
- A small in-process cache (5-minute TTL, keyed by a hash of the name and email claims) makes the
  normal case free: no database query per request. If your claims change (account renamed), the
  hash differs and the row is refreshed.

Why it matters: the translation list must show *other people's* names ("submitted by X,
approved by Y"). Your token proves who *you* are, but cannot resolve other users — so the TMS
keeps its own small copy of each user's public profile.

### 5.3 Registering a game version

1. Admin opens `/game-versions` on the frontend. The page shows every known version, newest
   first, with its status.
2. The register form is visible **only if the API said so**: the collection response carries a
   `register` link only for admins (Part 5.8).
3. Submitting calls `POST /api/v1/game-versions`. The handler validates the version text,
   canonicalizes it (`48.0` → `48`), rejects duplicates **after** canonicalization, and stores a
   new `Unprocessed` version.
4. A mistyped version can be deleted as long as it is not `Processed` **and** no translation row
   references it (#624). Both checks are server-side; the frontend's delete button is only
   rendered when the API sends a `delete` link.

### 5.4 The import — uploading a fresh export

The admin uploads `exported.txt` (~80 MB, ~790k lines) on `/import-export`, choosing the game
version. What happens inside `POST /api/v1/game-versions/{id}/import`:

**Pass 1 — plan (read everything, write nothing).**
The handler streams the uploaded file line by line, validating each row and building a compact
map *key → source hash*. It then streams the current catalog from the database as small value
structs, **not** through EF Core's normal query path (EF's retry logic buffers whole result sets,
and that buffer once killed the container — see below). The pure diff service compares both
sides and produces the plan.

Then the guards run: any parse error → reject everything (422); removed share over 20% without
the override flag → reject (`Import.MassRemovalBlocked`); version is Superseded → reject. Nothing
has been written yet.

**Pass 2 — apply (one transaction).**
Inside a single database transaction:
- **Added rows** stream straight into PostgreSQL's native bulk-load channel (**binary COPY**);
  the rows never exist as a full in-memory list (ADR-0011).
- **Source-changed / removed / restored rows** are loaded in chunks of 5,000, changed through the
  normal aggregate methods (`ApplySourceChange`, `MarkRemoved`, `Restore` — so every domain rule
  from Part 2 applies), saved, and released from memory before the next chunk.
- The version is marked **Processed**, and older still-Unprocessed versions are marked
  **Superseded** — in the same transaction as the data changes.

**After commit:** the artifact rebuild is *scheduled* (Part 5.6), and the admin gets an
`ImportSummary`: added / sourceChanged / invalidated / removed / unchanged / echoed counts, plus
warnings.

Details that make this flow production-grade:

- **Re-upload is idempotent.** Importing the same file again gives "unchanged" for every row and
  writes nothing — tests assert that even the `UpdatedAt` timestamps stay frozen.
- **Transient-fault safety.** The database connection retries on flaky networks. The trap: if
  the commit succeeds but the *acknowledgement* is lost, a naive retry would re-run with an empty
  change set and silently drop work. So the Unit of Work accepts the changes only after the commit
  is confirmed (`acceptAllChangesOnSuccess: false` + explicit accept), and Pass 2 can safely run
  again.
- **The upload cap is 256 MB** (configurable), about 3× today's file, on *both* hops (frontend and
  API), with a clean 413 beyond it (spec 0003).
- **The memory story (spec 0006):** the first real import crashed the production container with
  an out-of-memory kill, because four full copies of the file's data were alive at once. The fix
  was this two-pass streaming design: nothing may hold the whole file or the whole catalog.

Performance: a full baseline import (~790k rows) finishes in seconds against a real PostgreSQL in
the integration tests (budget < 10 s). On the live database it takes minutes, because the file is
read twice over the network.

### 5.5 Translating and approving

**Browsing.** `GET /api/v1/translations` — the public, paginated list. Filters: text search
(case-insensitive, over both English and Polish, with SQL wildcard characters escaped so a
literal `%` in game text matches itself), status filter, page size 1–100 (default 50). Sorting
always ends with a unique key, so pages never repeat or lose rows. Search is fast thanks to
trigram indexes (Part 6.2).

**Editing.** The editor page loads one row (`GET /api/v1/translations/{id}` — this one needs a
login) and shows English and Polish side by side, with every `<--DO_NOT_TOUCH!-->` placeholder
highlighted. For a NeedsReview row it also shows `PreviousSourceText`. Saving sends
`PUT /api/v1/translations` with `(FileId, GossipId, TranslatedText)`. The handler:

1. loads the row by fragment key — unknown key → 404 (rows are *born from imports*, never from
   the editor);
2. rejects a soft-removed row (422 `CannotEditRemoved`);
3. provisions the caller's Translator profile (Part 5.2) and stamps `SubmittedById`;
4. calls `ProvideTranslation(...)` → the row becomes **Draft** (whatever it was before — editing
   an Approved row un-publishes it);
5. if the row *was* Approved, schedules an artifact rebuild.

The text must not be empty and must fit the DAT (32,767 UTF-16 code units, ADR-0043). The
placeholder check on the frontend is **advisory**: if the Polish has a different number of
placeholders than the English, the editor warns but still lets you save. The API stores the text
exactly as typed.

**Approving.** `POST /api/v1/translations/{id}/approve` (admin only) calls `Approve(...)`: fails
without Polish text, fails on a removed row; otherwise sets **Approved**, stamps `ApprovedById`,
clears `PreviousSourceText`, returns 204 and schedules a rebuild.

**Bulk approving (spec 0007).** The list page lets an admin tick checkboxes and approve up to
**100 rows** (one page) in one `POST /api/v1/translations/approve`. Every row that is still
approvable (Draft or NeedsReview, not removed) is approved; the rest are counted as skipped — one
out-of-date row never blocks the batch. Response: `{requested, approved, skipped}`. One
transaction, and one rebuild scheduled only if at least one row was approved.

**Two people at once (optimistic concurrency).** Translation rows carry PostgreSQL's built-in
row-version column (`xmin`) as a concurrency token. If translator A and admin B load the same row
and both write, the second write sees that the row changed and fails with **409 Conflict**
instead of silently overwriting the first change. This closed a real audit finding: an approve
racing an import could overwrite the import's invalidation and publish stale Polish. The token is
a *shadow* property: the domain class knows nothing about it.

### 5.6 Rebuilding the distributed file (background, debounced)

Every write that changes what players get (import, approve, bulk approve, editing an approved
row) does **not** rebuild the file inline. It drops a signal into an in-process queue and returns.
A background worker:

1. waits a short **debounce window** (default 2 seconds) and collects everything that arrived —
   20 approve clicks in a row cause **one** rebuild;
2. queries the read model for all **Approved, not-removed** rows, ordered by
   `(FileId, GossipId)`;
3. serializes them into the `||` format (CRLF, `approved=1`), computes the SHA-256 content hash,
   and updates the single artifact row with a set-based UPDATE (never loading the old multi-MB
   content);
4. on failure, logs and **schedules itself again**, so the file always catches up.

Why (ADR-0021): an inline rebuild paid a full scan + serialize + hash on every approve, under a
process-wide lock. Trade-offs accepted: the file is *eventually consistent* (a download a second
after an approve may be one window behind), and the queue is in-process, which assumes a **single
API instance** (written down as a blocker to revisit before scaling out).

### 5.7 Distribution — how players get the file

`GET /api/v1/translation-files/polish` — anonymous, and deliberately boring:

- The response is the pre-built file with an **ETag** header equal to the content hash. A full
  download streams a copy from the API's own disk, written once per content hash (ADR-0064).
- The patcher stores the ETag next to its cached copy. Next time it sends
  `If-None-Match: <etag>`; if nothing changed, the server answers **304 Not Modified** with no
  body. The 304 decision reads only the hash column, never the multi-MB content.
- The endpoint does **no work per request** — no query over translations, no serialization. A
  thousand players hitting it on patch day read one small row (spec 0001).

**The player's `launch` flow** ties it together: find the download URL in the TMS discovery
document by its rel name, never by a hard-coded path (#611); sync the file (**304 → use the
cache; 200 → save the new file and ETag; network down → use the cached file and continue** — a
launch is never blocked by the server); refuse to run if the game is already open; hash the local
file and compare with the last patched hash; **re-patch only if it changed**; start the game's
official launcher and exit. Two hardening rules on the sync (AUDIT-SEC-01): the TMS URL must be
**HTTPS** (plain HTTP only for loopback), and the downloaded content's SHA-256 must **match the
ETag**, or the download is rejected and the cached file is used. The CLI's default TMS URL is
empty until the public URL is final, so sync runs only when `--tms-url` is passed.

The frontend also serves the same file at `/download/polish.txt` for people who want it from the
website.

### 5.8 HATEOAS — the API tells the UI what is allowed

Every read endpoint can include a `links` array: *self*, plus **only the actions the caller may
perform on that resource in its current state**. The link factory checks the target endpoint's
own authorization policy before it emits a link (ADR-0040). Examples: a translation row carries
`upsert` for translators, plus `approve` only for an admin on a Draft or NeedsReview row; the
versions list carries `register` only for admins; a soft-removed row carries only `self`.

The frontend renders buttons **only for links the API sent** — it never re-computes "am I an
admin?" itself. One brain (the server) decides permissions, so the UI cannot drift from it. Links
are opt-in via a vendor media type in the `Accept` header
(`application/vnd.dev-lotrokoniecdev.hateoas.json`); plain `application/json` responses carry no
links. Clients find every entry point through the discovery document, by rel name — there is no
API gateway (ADR-0041). Rel names are therefore a public contract that never gets renamed.

---

## Part 6 — The database

### 6.1 Two databases, one server

- **`lotro_translation`** — the TMS. Schema `translation`, four tables:
  `Translations` (~790k rows), `GameVersions`, `Translators`, `TranslationArtifacts` (one row
  per language — the pre-built file).
- **`lotro_auth`** — the auth system: ASP.NET Identity tables (users, roles), OpenIddict tables
  (clients, authorizations, reference tokens), the sign-in sessions (ADR-0062), and the outbox and
  inbox tables of the messaging pipeline.

Two databases because the two systems are separate worlds — separate migrations, separate
lifecycles, one server for cost. In production both live in one **Neon** project (serverless
PostgreSQL 18). A point-in-time restore rewinds *both together*, which is correct because one
migrator run migrates both.

### 6.2 Mapping choices worth knowing

- **Value objects** map with `ComplexProperty` by default (the semantically correct mapping —
  pure value, no identity). A VO that needs a **database index** maps as `OwnsOne`, because EF
  Core 10 cannot index complex properties. That is why `FragmentKey` (unique index),
  `TranslationSource` (search index) and `LotroNotationVersion` (unique index) are owned, while
  `DisplayName`/`Email` are complex properties.
- **Enums are stored as strings** (`Status = 'Approved'`) — readable in any SQL console, safe
  against renumbering.
- **All timestamps are `timestamptz` normalized to UTC** by a global converter.
- **Indexes:** unique `(FileId, GossipId)`; unique version; unique `IdentityId` on Translators
  (the provisioning idempotency key); unique `Language` on artifacts; **trigram GIN indexes**
  (`pg_trgm`) on `SourceText` and `TranslatedText`, which make the `%term%` search fast; a partial
  index on `Status` for non-removed rows (the only rows lists show); and real foreign keys on the
  three GameVersion columns of `Translations` (AUDIT-EF-05).
- **A `CHECK` constraint caps `TranslatedText` at the DAT limit** (ADR-0043). It is a `CHECK`
  and not `varchar(n)`, because changing the column type would rewrite the big table during a
  deploy.
- **`xmin` concurrency token** — see Part 5.5; no physical schema cost.

### 6.3 Migrations — forward-only, N-1 compatible

Rules (ADR-0023, proven in CI by ADR-0024):

- **Migrations only ever roll forward.** `Down()` is never run outside a local sandbox. In a
  shared environment, undoing a migration after the app wrote data is how you corrupt a database.
  Recovery is: fix forward, or restore the database to a point in time.
- **Every migration must keep the *previous* app release working** ("N-1 compatible"). During a
  deploy the migration runs *first*, while the old code still serves traffic; and if the new code
  fails, the rollback brings back the old code **on the new schema**.
- Destructive changes (drop or rename a column, add NOT NULL, …) therefore ship as
  **expand → backfill → contract** across at least two releases.
- **Enforcement is layered:** a CI text scan flags destructive statements in new migrations
  (override = an in-file `MIGRATION-SAFETY: acknowledged — <reason>` comment), and a CI job on
  migration PRs **runs the previous release's integration tests against the new schema**. Because
  production promotion is batched, the production deploy repeats that proof against the release
  that is actually serving (#534).
- Migrations run in a **one-shot migrator container before the app containers are recreated**.
  The deploy stops if the migrator fails, so a bad migration leaves the previous release serving.
  Right before it runs, a Neon snapshot branch is cut as a restore point. (auth-api's startup seed
  also calls `MigrateAsync`, which then finds nothing left to do.)

---

## Part 7 — Authentication and authorization, in depth

The full walk-through, in Polish, is [auth-tutorial.md](auth-tutorial.md); the HTTP contract is
[API.md §2](API.md#2-authentication--authorization-model). This part is the short version.

### 7.1 The three-actor model

Standard OAuth2/OIDC roles, self-hosted (ADR-0067 says who checks what):

- **auth-api** is the *authorization server* (OpenIddict on top of ASP.NET Identity). It owns
  passwords, accounts, and token issuing. Nobody else ever sees a password.
- **frontend** is the *client* — the cookie + OIDC flow from Part 5.1. It checks only what
  OpenID Connect asks of a client; the access token is opaque to it.
- **tms-api** is the *resource server*. It validates access tokens **offline**: it downloads
  auth-api's public keys (JWKS) and checks each token's signature, issuer, audience
  (`lotrokoniecdev-api`), type and expiry locally. If auth-api goes down, issued tokens keep
  working until they expire.

Why self-hosted instead of Auth0/Entra: full control, no per-user cost, no external dependency.
The trade-off (we own security updates and key management) is accepted; Part 12, question 10.

### 7.2 Tokens, keys, sessions

- **Access token**: signed JWT, 5 minutes, **signed but not encrypted**
  (`DisableAccessTokenEncryption`) so any standard JwtBearer middleware can validate it. Readable,
  so never put secrets in claims. Nothing checks it against the database, so its lifetime is the
  delay on every session revocation (ADR-0049).
- **Refresh token**: a *reference* token — the client holds a random handle; the real state lives
  in the auth database. Revocable (signing out revokes the authorization of this device's session
  and its tokens, so only this device is signed out — #931) and **rolling**.
- **Keys**: Development and Testing use throwaway keys generated at startup. Every other
  environment needs a real RSA signing key (≥2048 bits) and an AES encryption key from its
  environment file, with a slot for the *previous* signing key so keys can rotate without signing
  everyone out. The RSA objects are never disposed on purpose — OpenIddict needs them for the whole
  process to serve JWKS.
- **Session cookies**: the auth server's own login cookie is strict (HttpOnly, SameSite=Strict,
  30-minute sliding) and re-validates the user's **security stamp on every request**, so a
  password change or account deletion kills every live session at once. The cookie carries only a
  session key: the session itself lives in the auth database, encrypted, and every sign-in gets a
  new one (ADR-0062). Signing out deletes this browser's session on the server, so a stolen copy
  of the cookie stops working too, and the other devices stay signed in (#931).
- **Data Protection keyrings** (the keys that encrypt cookies and antiforgery tokens) live on a
  mounted volume in every deployed environment, with a fail-fast startup check. Without this,
  every redeploy would sign everyone out (ADR-0005).

### 7.3 Accounts (ADR-0022 and GDPR)

- **Email is the login**; it must be confirmed before the first sign-in and is unique at the
  database level (case-insensitive). A user can change it from "Moje konto": the change waits for
  a link sent to the new address, and the old address gets a 14-day link to undo it (ADR-0048).
- **Username is a display-only name**: unique, ASCII letters and digits only, enforced from one
  shared constant in three layers (validator, register page, Identity options). Emails contain
  `@` and usernames cannot, so the two never collide.
- **Passwords**: 8–128 characters with complexity, and never one found in a known data breach
  (checked against Have I Been Pwned when a password is set; a check that cannot run lets the
  password through — ADR-0066).
- **Registration** requires accepting the privacy policy and the terms of service, and asks for
  no data-processing consent, because the account runs on the contract (ADR-0068). Its answer never
  reveals whether the address already has an account: a taken address gets a notice by e-mail
  instead of a new account.
- **E-mails** go through an outbox in the same database transaction as the change, and a relay
  sends them through RabbitMQ after the commit (ADR-0035, ADR-0038). In dev they land in mailpit.
- **Export and deletion**: users can export their data and delete their account. Deletion is
  **two-phase** (ADR-0031): the request schedules erasure behind a 14-day cancellation window —
  the account is locked, sessions and tokens are revoked, and a one-time cancel link is e-mailed.
  After the window a background finalizer anonymizes the account and tells the TMS, which removes
  the name and e-mail from the translator profile (ADR-0065). Cancelling restores the account but
  forces a password reset, since the request may have come from a stolen password.

### 7.4 Abuse protection

- **No account enumeration**: login failures return one generic message; a login for a
  non-existent account still checks a dummy password hash; "forgot password" and "resend
  confirmation" always claim success; and every anonymous answer that hides whether an account
  exists waits for a minimum time (ADR-0059).
- **Lockout**: 5 failed attempts → 5-minute lock. The email-confirmation check runs *after*
  password verification — otherwise it would be a timing oracle.
- **Rate limits**: per client address on both APIs, plus per-inbox and per-account budgets for
  mail and password checks (ADR-0053, ADR-0055, ADR-0057). The frontend forwards the visitor's
  address with a shared key, so all signed-in users do not share one bucket (ADR-0054). The full
  table: [API.md §2.4](API.md#24-rate-limiting). The limiter runs *before* authentication, so junk
  traffic is counted and rejected cheaply.
- Daily background jobs prune expired OpenIddict tokens and old sign-in sessions, so the auth
  tables do not grow forever.

### 7.5 Authorization on the TMS side

- **Default-deny**: the fallback policy demands an authenticated user; public endpoints opt out
  explicitly (`AllowAnonymous`) — the safe direction of mistake.
- Policies: `RequireAdminRole` (role Admin), `RequireTranslatorRole` (Admin **or** Translator),
  `RequireAuthenticatedUser` (any signed-in user — the contribution data export). Claims stay in
  their raw OIDC form (`MapInboundClaims = false`), so `sub`, `name` and `role` mean the same
  thing everywhere.
- `CurrentUserAccessor` reads the caller's identity once per request; write handlers stamp
  `SubmittedById`/`ApprovedById` from it — every content change is attributable to a person.

---

## Part 8 — The frontend

### 8.1 Static SSR — the deliberately boring choice

The frontend is **Blazor Static Server-Side Rendering**: every page is rendered to plain HTML on
the server and sent to the browser. There is **no WebSocket circuit, no WebAssembly, no
JavaScript interactivity, no per-user server state**. Forms are classic HTML `POST`s.

Why: the UI is CRUD — lists, filters, an editor form, buttons. Static SSR makes it fast (nothing
to hydrate), cheap (no live connections), simple (request in, HTML out), and robust (a browser
refresh is always safe thanks to Post-Redirect-Get). The cost — no rich client-side
interactivity — is acceptable; the one place it shows is "select all pages" features, which are
out of scope.

This is **enforced**: `scripts/check-ssr-purity.sh` runs in CI and fails the build on
`@rendermode`, interactive `@on*` handlers, `StateHasChanged`, or an inline `<script>` that the
Frontend's CSP would block (#670). Wanting interactivity means writing an ADR first.

### 8.2 The pages

| Route | Who | What it does |
|---|---|---|
| `/` | public | Landing page with a progress meter (approved / awaiting / total; from a 30 s server-side snapshot — AUDIT-EF-04) and download links |
| `/translations` | public | Searchable, filterable, paginated list; per-row Edit link; admin sees bulk-approve checkboxes |
| `/editor/{id}` | logged in | Side-by-side editor: English (and previous English for NeedsReview) vs Polish; placeholder highlighting and mismatch warning; Save + Approve |
| `/dashboard` | logged in | Progress tiles: total / translated / approved / remaining |
| `/game-versions` | logged in | Version list; admin-only register form and guarded per-row delete |
| `/import-export` | logged in | Download `polish.txt`; admin-only upload of `exported.txt` with an import summary |
| `/account` and its sub-pages | logged in | "Moje konto": account data, data export, password change, e-mail change, account deletion (spec 0009, spec 0013) |
| `/regulamin`, `/terms` | public | Terms of service (spec 0010) |
| `/download/polish.txt` | public | The distributed file, served for people |
| error pages | public | Friendly 400 / 403 / 404 / 500 / "login unavailable" (503) pages |

The login, registration, password reset and privacy policy pages belong to auth-api, not to the
frontend (`/Account/...` on the auth origin).

### 8.3 How pages get data and post changes

- A typed HTTP client (`ITranslationSystemClient`) calls tms-api. It is wrapped in a **Polly
  resilience pipeline**: 2 retries with backoff for safe requests, a circuit breaker, and a
  timeout that fits the call — 10 s for JSON calls, **5 minutes for the multipart import upload**.
- Delegating handlers attach the user's bearer token, the HATEOAS `Accept` header and the
  visitor's address for rate limiting to every call, and refuse to send anything to another
  origin — pages never touch tokens. If a call comes back 401, the session is marked dead so the
  next page load signs the user out cleanly instead of looping.
- Results come back as `ApiResult<T>` — the frontend's small Result type carrying
  `ProblemDetails` on failure. The frontend owns the Polish text for every failure (ADR-0044), so
  transport failures show friendly messages ("usługa niedostępna", "przekroczono czas") instead of
  stack traces.
- Forms are plain SSR: `<form method="post" @formname="...">` + antiforgery token + hidden
  fields, bound via `[SupplyParameterFromForm]`. The one `EditForm` (the import upload) emits its
  own antiforgery token — a manual one there would double it and break the request.
- Every successful POST ends in **Post-Redirect-Get** with a query flag (`?approved=12`) —
  refresh-safe, and the redirected GET fetches fresh HATEOAS links, so the buttons always match
  the new state.
- Page logic that deserves tests (progress math, placeholder analysis, list view models) lives in
  **pure C# classes** next to the `.razor` files, unit-tested without a browser.

---

## Part 9 — Testing

### 9.1 The philosophy

The full rules are in `CLAUDE.md` → "Testing philosophy" and `tests/CLAUDE.md`. In short:

- **Test the behavior, not the implementation.** Tests call the public surface (a handler, an
  endpoint, a parser) and assert what comes out (`Result`, persisted state, HTTP response). Mocks
  are used only for **genuine boundaries** (the DAT file handler, the forum fetcher). So a
  refactor that changes *how* without changing *what* breaks no tests.
- **`.Received()` (did-you-call-it assertions) is almost forbidden** — allowed only for side
  effects invisible in the return value.
- **Unhappy paths are first-class.** Boundary matrices (`[Theory]` + `[InlineData]`): empty, too
  long, malformed, already in that state.
- **AAA (arrange–act–assert), assertions inline**, one reason to fail per test. Tooling: xUnit,
  Shouldly, NSubstitute. Naming: `Method_Scenario_ExpectedResult`.
- **Unit tests are pure** — no filesystem, no network, no database.
- **The whole suite runs locally before every push**, not just the part you think the change
  touches.

### 9.2 The test pyramid (15 projects)

| Level | Projects | How they run |
|---|---|---|
| Unit — patcher | `Tests.Unit` | pure; handlers with real validators + mocked boundaries |
| Unit — TMS | `SharedKernel.Tests.Unit`, `TranslationSystem.Domain.Tests.Unit`, `TranslationSystem.API.Tests.Unit`, `AuthSystem.API.Tests.Unit`, `Frontend.Tests.Unit`, `Logging.Tests.Unit` | pure; fake read-DbContext; bUnit component tests for Blazor |
| Unit — architecture | `Architecture.Tests.Unit` | pure; the structural house rules checked over assembly IL (NetArchTest) |
| Integration | `TranslationSystem.API.Tests.Integration`, `AuthSystem.API.Tests.Integration`, `Frontend.Tests.Integration` | the real app in-process (`WebApplicationFactory`). The two API suites run against a **real PostgreSQL in Docker** (Testcontainers), with auth faked by self-signed test tokens; the Frontend suite boots the Frontend host alone, with no database (#915) |
| End-to-end — TMS | `TranslationSystem.E2E.Tests` | the real container images on a private Docker network; **real tokens** issued by the real auth-api and validated by tms-api via live JWKS |
| End-to-end — browser | `Frontend.E2E.Tests` | full stack + a headless Chromium via Playwright, all inside Testcontainers (ADR-0009): register → confirm email via mailpit → login → logout |
| End-to-end — patcher | `Tests.E2E`, `Tests.Infrastructure` | Windows only; `Tests.E2E` also needs a real DAT file and skips its tests without one (`SkippableFact`). Off Windows, `dotnet test` compiles both but runs neither (#816) |

Also notable:

- **Contract parity tests** (Part 2.5) pin the `||` format across the two bounded contexts.
- **Snapshot tests** (Verify) pin large API responses and rendered pages; they never replace an
  assert (#571).
- **Mutation testing** (Stryker) runs on the Domain and SharedKernel projects in CI: it plants
  small bugs and fails the build if the tests catch fewer than 67% of them — a measure of test
  *strength*, not just coverage.
- **The N-1 seam**: integration test factories can be pointed at external schema scripts
  (`N1_COMPAT_SCHEMA_SCRIPTS_DIR`), which is how CI runs *yesterday's tests against tomorrow's
  schema* (Part 6.3). In normal runs the seam does nothing.

---

## Part 10 — Running it on your machine

The full command list is in `CLAUDE.md` → "Commands"; this is the short tour.

### 10.1 Prerequisites

.NET 10 SDK · Docker · one-time `dotnet dev-certs https --trust` (so the three local apps can
serve HTTPS with the standard ASP.NET development certificate).

### 10.2 The development loop

```bash
scripts/up.sh          # starts INFRA ONLY: postgres, rabbitmq, one-shot migrator, mailpit, aspire
                       # (first run copies .env.example → .env)

# then the three apps run directly on your machine (hot reload, breakpoints):
dotnet run --project src/AuthSystem/LotroKoniecDev.AuthSystem.API                  # :5003
dotnet run --project src/TranslationSystem/LotroKoniecDev.TranslationSystem.API   # :5002
dotnet run --project src/Frontend/LotroKoniecDev.Frontend                         # :7017
# (or the Rider compound run configuration ".run/TMS dev (all hosts)")
```

| URL | What |
|---|---|
| https://localhost:7017 | the web app |
| https://localhost:5002 | tms-api (`/health`, and Scalar API docs in Development) |
| https://localhost:5003 | auth-api (login/register pages) |
| http://localhost:8025 | mailpit — the fake inbox (confirmation emails land here) |
| http://localhost:15672 | RabbitMQ management UI |
| http://localhost:18888 | Aspire dashboard — logs and traces |

Why the apps run on the host and only infrastructure runs in Docker (ADR-0006): containers gave
neither a fast inner loop (no hot reload, image rebuilds) nor production parity, and a
containerized OIDC client breaks the "one Authority URL for browser and back-channel" rule that
`localhost` satisfies naturally. The parity job belongs to the next stack.

First login: register an account on the frontend (it becomes a **Translator**; confirm the email
in mailpit), or configure `AdminUser:Email`/`AdminUser:Password` for auth-api (in
`appsettings.Local.json`; only Development and Testing read the password — ADR-0056) and the
seeder creates an **Admin** at startup.

Known local pitfall: if PostgreSQL answers with error `28P01` (password authentication failed),
your `.env` password does not match the one the Postgres volume was created with — recreate the
volume or align `.env`; re-running `down -v && up` with the *same wrong* `.env` changes nothing.

### 10.3 The production-parity stack

```bash
scripts/up-prod.sh --build
```

A **separate** compose file runs all four images (auth, tms, frontend, migrator) **plus a Caddy
reverse proxy** and RabbitMQ, under `ASPNETCORE_ENVIRONMENT=Production`: real OpenIddict keys
(generated into `.env.prod` on first run), TLS from a locally created CA, hosts-file entries
`app|auth|tms.lotro.test`, persistent keyring volumes, PostgreSQL over SSL. Purpose: catch
production-only breakage (HTTPS-only OpenIddict, forwarded headers, keyrings, cert trust) on a
laptop before any deploy. The proxy gives every app **one public origin**, reachable the same way
from the browser and from inside the network — which is how the containerized-OIDC problem goes
away in real production too.

---

## Part 11 — CI/CD and production

### 11.1 The pipelines (GitHub Actions, 14 workflows)

| Workflow | When | What it guards |
|---|---|---|
| `pr-verify` | every PR | **the merge gate** ("Pull Request Verification"): the cheap script guards (SSR purity, client hypermedia, Docker restore graph, migration safety, loop provenance) and their self-tests, a Release build with **zero warnings**, unit tests and integration tests — each run only when the diff can break it; plus a build and Trivy scan of each image when the images can change. The details are in `CLAUDE.md` |
| `ci` | push to main | the build and both test suites after the merge; CD starts only from a green CI |
| `cd` | after CI succeeds | build 4 images once → security scan (Trivy, fails on fixable HIGH/CRITICAL) → sign (cosign, keyless) + provenance + SBOM → auto-deploy **staging** → wait for human approval → deploy **production** |
| `deploy` | reusable | the rollout over ssh described below |
| `n1-compat` | PRs touching migrations | previous release's integration tests against the new schema (Part 6.3) |
| `mutation-test` | PRs touching Domain/SharedKernel | Stryker, break at 67% |
| `e2e` | manual + PRs touching package/Docker dependency manifests | full-stack and browser E2E suites |
| `smoke` | after deploys | health + a real OIDC token round trip + file distribution |
| `health-ping` | daily cron | probes the three public origins once a day (deep `/health`, with the health check key) |
| `cd-janitor` | nightly cron | cancels CD runs left waiting at the `production` approval gate for more than 24 h, so an old candidate can never be approved by accident (#527) |
| `codeql` | PRs + weekly schedule | static security analysis; no PR merges with an open alert |
| `gitleaks` | PRs/pushes | secret scanning |
| `actionlint` | every PR | lints `.github/workflows/` so workflow-only PRs cannot merge broken |
| `labeler` | PR opened/reopened | adds `needs-windows-check` when a PR changes Windows-only patcher code, which CI cannot run (#1250) |

### 11.2 How a release reaches production

1. Merge to `main` → CI green → CD builds **one immutable image per commit** (`sha-<short>`).
2. Images are vulnerability-scanned, signed, and published with provenance — the deploy later
   **refuses an image our own CD did not build and attest** (fail-closed supply-chain check).
3. **Staging deploys automatically.** Staging is a full, separate environment (own box, own
   database, own secrets, own domain `staging.lotro-translator.pl`).
4. A human clicks **Approve** in GitHub → the *same images* are promoted to production. Approvals
   are batched, so one promotion may carry several merges.
5. Each deploy runs the same steps (`deploy.yml`, `scripts/hetzner/deploy.sh`): for production,
   prove the N-1 rule against the release that is actually serving (#534) → resolve the tag to
   digests and verify provenance → cut a Neon snapshot branch → copy the stack files to the box
   over ssh → pull the images and check their digests → run the **migrator as a gate** →
   `docker compose up -d` → reload Caddy → smoke the public origins.
   **Any failure → automatic rollback** of the images *and* the config to the previous deploy.
   Rollback moves *code only* — never the schema (that is why N-1 compatibility exists).

There is no blue/green: a 4 GB box cannot hold a second live set of apps, so `up -d` recreates
the containers in place, with a few seconds of downtime per app, and the smoke runs after the new
containers are live. The migration gate and the automatic rollback make up for it (ADR-0034).

### 11.3 The production platform

The operations manual is `docs/deployment/runbook.md`; this is the overview.

- **Hetzner boxes running `docker compose` behind Caddy** (ADR-0034). Two CX23 boxes: one runs
  production, one runs staging — **one box = one environment**, and the box's `chmod 600`
  `/opt/lotro/.env` is what makes it production or staging. Caddy terminates TLS with automatic
  Let's Encrypt certificates, one vhost per app; only Caddy publishes ports, so the apps are
  reachable *only* through the proxy. There is **no IaC**: the "infrastructure" is a box, a compose
  file and an env file.
- **App code is cloud-agnostic** (ADR-0008): plain containers, HTTP on :8080, non-root, JSON logs
  to stdout, all configuration via environment variables, health endpoints. That is what made the
  Azure → Hetzner move a matter of re-pointing images and env vars, with **no application-code
  changes**.
- **Secrets** live in the box's `/opt/lotro/.env` (`chmod 600`, owner `deploy`), never in git.
  The move off Azure proved that no secret in this system is irreplaceable: none could be read back
  from the disabled Key Vault, and every one was created again.
- **Databases on Neon** (serverless Postgres): scale to zero when idle, 6-hour point-in-time
  restore on the free plan, plus the pre-migration snapshot branch every deploy cuts. Accepted risk
  (zero users): no off-platform backups yet; the revisit trigger is the first real translators.
- **Observability** (ADR-0050, ADR-0051): Grafana, Loki, Prometheus and Tempo run on the
  **staging** box, so a production outage does not take the evidence with it. An Alloy agent on
  each box collects the apps' OpenTelemetry data, and the production agent pushes it to staging over
  TLS. The stack is its own compose project (`compose.observability.yaml`), deployed by hand, never
  by CD. On top of that: the daily health ping and the post-deploy smoke.
- **FinOps.** A flat-price box removed the whole Azure cost problem class (scale-to-zero replicas,
  warm windows, revision sweeps — ADRs 0020/0027/0029, now obsolete). The one ruling that survives
  is ADR-0025: readiness probes stay **database-free**, because Neon still scales to zero and a
  frequent readiness ping would keep the database awake almost all the time.

---

## Part 12 — Defending the design (interview Q&A)

Short, honest answers with trade-offs. Each maps to an ADR or spec you can cite.

**1. Why two bounded contexts sharing a *file*, not one system or a shared database?**
Because the two halves have incompatible runtimes: the patcher must run 32-bit Windows-native
(x86 `datexport.dll`); the TMS is Linux containers + PostgreSQL. Any shared code layer would hurt
one side (Windows dependencies in Docker, or web dependencies on a gaming PC). A version-controlled
text format is the loosest possible coupling, testable with golden fixtures on both sides, and it
matches the real workflow (files already travel between machines). Trade-off: two parsers can
drift — guarded by parity tests, and the format changes only via ADR. *(ADR-0002)*

**2. Why no MediatR?**
The mediator added indirection nobody used: no pipeline behaviors in real use, wiring invisible
until runtime, "find usages" broken, plus a vulnerable transitive dependency. We kept what matters
from CQRS — one use case = one record + one handler — and inject the **closed handler interface**
directly. Compile-time wiring, greppable call sites, one explicit DI line per use case. Trade-off:
no single choke point for cross-cutting concerns — accepted, because validation and logging live in
handlers by convention. *(ADR-0001)*

**3. Why Result instead of exceptions?**
Business failures are expected outcomes, not emergencies: "cannot approve without text" is domain
information. Results make failure part of the method signature, so callers must handle it.
Exceptions remain for programmer errors only (guards). The API maps `Error.Type` to HTTP codes in
one place, so the error contract is uniform. Trade-off: some ceremony (`IsFailure` checks).

**4. Why CQRS (separate read models) from day one, in a small system?**
Because the two sides want opposite things: writes want small, rule-enforcing aggregates; lists
want flat, join-shaped, filterable rows. Same tables, two mappings — no sync, no duplication.
Query handlers *cannot* mutate (no-tracking context, no behavior methods). Cost: a read model +
config per aggregate — minutes of work each. *(ADR-0002 amendment)*

**5. Why vertical slices instead of layered architecture?**
Change locality. A feature request touches one file (+ its DTOs and tests), not five layers.
Deleting a feature is deleting a file. Slices share the domain and infrastructure underneath, so
rules are not duplicated — there is just no *horizontal* layer of services that every change must
tunnel through.

**6. Why hand-written strongly-typed IDs?**
Type safety (a `TranslationId` is not a `GameVersionId` — compiler-enforced), GUID v7 for
index-friendly ordering, and two factories: validated `Create` for untrusted input, unvalidated
`FromValue` for trusted rehydration. The library we used first was abandoned and its generated
public constructor *skipped validation* — 30 lines per ID is cheap ownership. *(ADR-0010)*

**7. Why is the distributed file a "projection", not an aggregate?**
It guards no rule and is fully re-computable from translations — derived data. Modeling it as an
aggregate (as first happened) polluted the domain and misused the repository abstraction. It
became an immutable row behind a tiny store port. General lesson: **not everything is DDD** —
aggregates are for protected state. *(ADR-0007)*

**8. Why rebuild that file in the background with a debounce?**
The inline version paid O(catalog) serialize + hash on *every* approve, under a process lock —
k rapid approvals = k full rebuilds, and a client disconnect could leave the file stale. Now
writes only signal; a worker merges a burst into one rebuild ~2 s later and retries on failure.
Accepted: eventual consistency (seconds) and a single-instance assumption — both written down with
revisit triggers. *(ADR-0021)*

**9. Why ETag + a pre-built file for distribution?**
Patch-day traffic is many clients asking "anything new?". The answer must cost nothing: the file
is built once per change, served from a disk copy, and `If-None-Match` turns the common case into
a 304 with no body. The CLI keeps a cached copy and works offline — a launch never blocks on the
server. *(spec 0001, ADR-0064)*

**10. Why self-hosted OpenIddict rather than Auth0/Entra?**
Control (custom Polish UX, custom rules like ADR-0022), no per-user cost, no external dependency
for a hobby-scale system. Standards-compliant OIDC means moving to a SaaS later is possible. Owned
risk: key management and security updates are ours; mitigated by fail-fast key validation,
rotation support, and integration tests against the real server.

**11. Why lazy provisioning instead of creating the TMS profile at registration?**
Registration-time provisioning is a distributed transaction across two systems — if the TMS is
down, registration breaks, and back-fill is manual. Lazy get-or-create on the first authenticated
request is idempotent (unique index + race handling), self-healing (profiles catch up whenever
claims change), and needs no cross-system saga. First tried "on first write"; users who only
browsed then had no profile — amended to "first request". *(ADR-0004)*

**12. Why email login with a separate display username?**
The implementation had drifted: login by username, charset by framework accident, and duplicate
case-variant emails possible — which would lock users out for good once login moved to email. The
ADR made the product rule physical: email = unique login (database-level unique index), username =
unique ASCII display name, enforced from one shared constant in three layers. *(ADR-0022)*

**13. Tell me about a production incident.**
The first real 79 MB import killed the API container (0.25 vCPU / 0.5 GB at the time) by running
out of memory: the handler held the file and the catalog several times over. Diagnosis found a
second, hidden copy: EF Core's retrying execution strategy *buffers entire result sets*, so even
the "streaming" catalog read was buffered — the fix uses a raw data reader for that one query. The
redesign (spec 0006) made the import two-pass and chunked: nothing may hold the file or the catalog
in memory; added rows stream into binary COPY; changes apply in 5k chunks with the change tracker
cleared. A temporary "give it 4 GB" bridge was reverted the same week. *(ADR-0011, specs 0004/0006)*

**14. Why forward-only, N-1 compatible migrations?**
Because the deploy migrates the schema *before* the new code runs, and rollback brings back *old
code on the new schema*. So every migration must keep the previous release alive, and `Down()` is
a lie in shared environments (it cannot un-write data). Destructive changes ship as expand →
backfill → contract. Enforced twice: a CI text gate with an explicit in-file acknowledgement, and
an executable proof — the previous release's integration suite runs against the HEAD schema in CI,
and again at production promotion. *(ADR-0023, ADR-0024)*

**15. Why `xmin` for concurrency instead of a version column?**
PostgreSQL already keeps a row version (`xmin`) on every row — no schema cost, no code in the
domain (a shadow property in the EF mapping), and it closes a real race: approve vs import
overwriting an invalidation. Stale writes come back as 409, and the client re-reads. Optimistic
concurrency fits because conflicts are rare; locking would punish the common case.

**16. Why HATEOAS?**
One brain for permissions. The server works out which actions exist for *this caller* on *this
resource in this state* and sends them as links; the UI renders only what it received. No
duplicated role or status logic to drift. With no API gateway, the discovery document is also how
every client finds its entry points. *(spec 0002, ADR-0040, ADR-0041)*

**17. How do you *know* translations survive game updates?**
Empirically. Nine live tests across real updates (including the majors 47.2→48.0 and 48.8→49.1)
checked the result through several independent channels: in-game text, export presence, diff
content, and launch logs. Root cause understood: the launcher patches the DAT in chunks, touching
only changed parts. Also proven: the DAT's internal version number is *useless* as a content
signal (unchanged across years of updates) — the release-notes title is the only reliable version
source. These findings killed whole planned features (file protection, vnum triggers,
re-patch-after-update) — recorded in `docs/knowledge-base/` so nobody re-invents them. *(Lesson:
measure before building.)*

**18. What are the known limits, and when do they bite?**
Accepted on purpose, each with a written trigger: a single API instance (in-process rebuild queue)
— bites at horizontal scale; no blue/green, so a deploy costs seconds of downtime — fine with no
users (ADR-0034); no off-platform database backups beyond Neon PITR + snapshot branches — revisit
at the first real users; Neon scales to zero, so the first request after a quiet period waits for
the database to wake up; version registration and the export upload stay manual (ADR-0030).
Knowing *where the cliff is* is part of the design.

---

## Part 13 — Glossary

| Term | Meaning |
|---|---|
| **ADR** | Architecture Decision Record — a short document: context, decision, consequences. The list is in `docs/adr/` (map: Appendix A). |
| **Aggregate** | A small cluster of domain data changed only through methods on its root object, which enforce the business rules. |
| **Artifact** | The pre-built `||` file with all approved translations, served to patchers. |
| **Bounded context** | A self-contained model world with its own language and code. Here: the patcher and the TMS. |
| **CQRS** | Separating writes (commands, via aggregates) from reads (queries, via read models). |
| **DAT file** | LOTRO's giant binary container holding all game assets, including texts. |
| **Debounce** | Waiting a short moment to merge a burst of triggers into one action. |
| **Echo** | A row in the export whose "English" is our own Polish, because the admin exported from a patched game (spec 0012). |
| **ETag / If-None-Match** | HTTP revalidation: the server labels content with a hash; the client asks "changed since this label?"; "no" costs almost nothing (304). |
| **FragmentKey** | `(FileId, GossipId)` — the stable identity of one game text across versions. |
| **HATEOAS** | Responses carry links describing what the caller may do next; the UI follows links instead of hardcoding rules. |
| **Idempotent** | Safe to run twice — the second run changes nothing (re-import, provisioning). |
| **Invalidation** | Marking a translation NeedsReview because its English source changed. |
| **JWKS** | The auth server's published public keys; resource servers use them to verify token signatures offline. |
| **JWT** | A signed token (`header.payload.signature`) carrying claims like `sub` and `role`. |
| **Migration (N-1 compatible)** | A schema change that the *previous* app release can still run against. |
| **OIDC / OAuth2** | The standard protocols for login and delegated access; we use the authorization-code + PKCE flow. |
| **Outbox** | A table where a change records the message it must send, in the same transaction; a relay sends it after the commit. |
| **PKCE** | A one-time secret proving the same app that started a login finishes it. |
| **Placeholder** | `<--DO_NOT_TOUCH!-->` — where the game inserts a runtime value into a text. |
| **Projection** | Derived, regenerable data (our pre-built file) — deliberately not an aggregate. |
| **Read model** | A flat, behavior-free record mapped over the same table as the write model, used by queries. |
| **Reference token** | A token whose state lives server-side (revocable), unlike a self-contained JWT. |
| **Repository** | The interface through which aggregates are loaded and stored. |
| **Result monad** | The success-or-error return type that replaces exceptions for business failures. |
| **Soft removal** | Stamping `RemovedInVersion` instead of deleting a row; reversible. |
| **Strongly-typed ID** | A dedicated struct per entity ID, so IDs cannot be mixed up. |
| **Testcontainers** | A library that starts real infrastructure (PostgreSQL, browsers) in Docker for tests. |
| **Unit of Work** | The "commit button": collect changes, save once, one transaction. |
| **Value object** | An immutable type defined by its content, carrying its own validation (e.g. `LotroNotationVersion`). |
| **Vertical slice** | One use case in one file: endpoint + command/query + handler together. |
| **vnum** | The DAT file's internal version number — proven useless as a content version. |
| **xmin** | PostgreSQL's built-in row version, used as our optimistic-concurrency token. |
| **`\|\|` file** | The line-based text format both contexts exchange (Part 2.5). |

---

## Appendix A — every ADR in brief

The decisions themselves, with their reasons and amendments, live in `docs/adr/`. This is a map by
theme, so you know which one to open.

| Theme | ADRs |
|---|---|
| Architecture and code shape | 0001 (no mediator), 0002 (two bounded contexts; TMS started from an earlier project), 0007 (read projections are not aggregates), 0010 (hand-written strongly-typed IDs), 0021 (background artifact rebuild), 0040 (authorization-aware links, anonymous TMS root), 0041 (no API gateway), 0067 (who checks what) |
| Domain rules | 0003 (canonical game version, at most three segments), 0004 (lean `Translator`, provisioned lazily), 0030 (export and version registration stay manual), 0045 (the game version reaches clients through our API) |
| The `\|\|` file and the patcher | 0039 (backslash escape), 0042 (carve the line, never split), 0043 (over-long content refused at the boundaries), 0047 (`source_digest`: stale Polish never lands over changed English) |
| Data and import | 0011 (COPY for added rows), 0014 (Neon), 0023 (forward-only, N-1 migrations), 0024 (executable N-1 proof), 0064 (translation file served from a disk copy) |
| Accounts and sign-in | 0022 (email is the login), 0031 (14-day deletion grace period), 0032 (GDPR export with a TMS part), 0046 (unconfirmed-email login answer), 0048 (e-mail change and undo), 0049 (5-minute access token), 0052 (export behind the password), 0053 (password confirmation budget), 0056 (admin seeded without a password), 0059 (time floor on anonymous answers), 0061 (introspection reads only POST), 0062 (sign-in session in the database), 0063 (one-time links), 0065 (account erasure reaches the TMS), 0066 (breached passwords refused), 0068 (account runs on the contract) |
| Abuse limits | 0054 (frontend calls metered per visitor), 0055 (mail budget per recipient), 0057 (mail budgets per inbox) |
| Messaging | 0035 (signal-driven outbox relay), 0036 (dead-lettering with a delivery limit), 0037 (inbox de-duplication), 0038 (one pipeline for every e-mail) |
| Frontend | 0005 (Data Protection keys on a volume), 0006 (apps on the host in dev), 0009 (browser E2E in Testcontainers), 0044 (the frontend owns the Polish error text) |
| Deployment and operations | 0008 (cloud-agnostic apps), 0015 (derive dependency versions), 0018 (staging + two-stage promotion), 0025 (database-free readiness probes), 0028 (Docker restore graph is gated), 0034 (Hetzner instead of Azure), 0050 (observability backend on staging), 0051 (Tempo in the first cut), 0058 (full health check needs a key) |
| Process | 0026 (only maintainer-written issues drive the loop), 0033 (Avalonia for the desktop app), 0060 (the loop stops at the PR; approval by self-assign) |
| Obsolete with Azure (ADR-0034) | 0012 (CD pipeline — its shape stands, the rollout target changed), 0013 (Key Vault), 0016 (managed telemetry agent), 0017 (per-environment IaC), 0019 (external probes), 0020 (FinOps right-sizing), 0027 (warm window — the daily ping survives), 0029 (one active revision) |

---

## Appendix B — every spec in brief

Full documents in `docs/specs/`. A spec is written and *agreed* before implementation; open
questions go to the owner, never invented.

| Spec | Topic | State |
|---|---|---|
| 0001 | The game-update lifecycle: versions, the five-outcome diff, invalidation, fallback to English, the truncation guards, ETag distribution | Implemented, except the forum watcher (post-MVP) — the foundation of Part 2 |
| 0002 | HATEOAS links on read endpoints | Implemented |
| 0003 | Large export upload (256 MB cap, upload-aware timeout, clean 413) | Implemented |
| 0004 | Bulk set-based import, phase 1 (COPY for added rows) | Implemented |
| 0005 | Game-versions management UI | Implemented |
| 0006 | Streaming two-pass import, phase 2 (the OOM fix) | Implemented |
| 0007 | Bulk approve from the translations list | Implemented |
| 0008 | Game-content catalog (LOTRO Companion data as a lens over the rows, joined by `(FileId, GossipId)`) | Agreed; milestone M7, not started |
| 0009 | "Moje konto": GDPR self-service in the frontend | Implemented |
| 0010 | Terms of service | Implemented |
| 0011 | Legal risk mitigation pack | Implemented |
| 0012 | Update resilience on the player's machine | Agreed; its per-row source guard is built (ADR-0047) |
| 0013 | Self-service e-mail change | Agreed; built (ADR-0048) |

---

*End of the handbook. If something here disagrees with the code, the code wins — and this file
should be fixed in the same pull request.*
