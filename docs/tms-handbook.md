# The TMS Handbook — LOTRO Polish Translation Platform

> **One document to understand the whole system.** Written in simple English, from the bottom
> (the problem and the domain) to the top (deployment and operations). Use it to onboard a new
> team member in a day, or to prepare for an interview where you must explain and defend every
> design decision.
>
> Facts in this handbook come from the **code** (checked on 2026-07-06; re-audited 2026-07-11;
> deployment chapter rewritten 2026-07-13 for the Hetzner move — ADR-0034).
> When an older document disagrees with this one, trust the code first, then this handbook.

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
| 11. CI/CD and production | Pipelines, cloud, money |
| 12. Defend the design | Interview questions and strong answers |
| 13. Glossary | Every term, A to Z |
| Appendix A | All 31 architecture decisions (ADRs), each in a few sentences |
| Appendix B | All 10 feature specifications (specs), each in a few sentences |

**Shortcuts for different readers:**

- *New developer, first day:* Parts 1–5, then Part 10 to run it.
- *Interview preparation:* Parts 1–2 for the story, Part 12 for the answers, Appendix A for the decisions.
- *Operations / deployment:* Parts 6, 10, 11, plus `docs/deployment/runbook.md`.

**Where to go deeper.** This handbook is the entry point, not the only document. The repository
also has: `docs/DOMAIN.md` (domain deep dive, Polish), `docs/API.md` (full endpoint reference),
`docs/INVARIANTS.md` (every business rule with a file and line number), `docs/auth-tutorial.md`
(auth from zero, Polish), `docs/knowledge-base/` (results of real experiments with the game),
`docs/deployment/runbook.md` (operations manual), and `docs/adr/` + `docs/specs/` (the original
decision and specification documents). Note: a few older documents still describe the
translation-file rebuild as a synchronous step — the current, correct behavior is the
*background debounced rebuild* described in Part 5.6 of this handbook.

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
   review workflow, and publishes a single "official" translation file that every player's
   patcher downloads.

### 1.2 Why two separate systems?

The patcher and the TMS live in one repository but are **two separate worlds** (in architecture
language: two *bounded contexts* — see ADR-0002):

- The **patcher** must run on **Windows**, in **32-bit x86** mode, because it calls a native
  library (`datexport.dll`) that reads and writes the DAT file. It runs on a player's gaming PC.
- The **TMS** is a **web system**: PostgreSQL database, two APIs, and a web frontend. It runs in
  **Linux containers** in the cloud.

These two worlds **never share code**. They cannot: one needs Windows-native binaries, the other
runs on Linux. Instead, they share one **data contract**: a simple text-file format (the `||`
file, described in Part 2.5). The patcher writes that format; the TMS reads it — and the TMS
writes it back; the patcher reads it. Each side has its **own parser** for the format, and
special tests ("parity tests") make sure the two parsers always agree.

```
   WINDOWS GAMING PC                              LINUX CLOUD
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

- The **patcher is finished and proven**. Eight live tests against real game updates confirmed
  that it works and — an important discovery — that **translations survive game updates**
  (details in Part 12, question 17).
- The **TMS backend is built**: import, editor endpoints, approval, file distribution, auth.
- The **frontend is built**: home page, translation list, side-by-side editor, dashboard,
  game-version management, import/export page.
- The system **deploys automatically to a Hetzner VPS** (staging first, then production after a
  human approves). The public site is `https://lotro-translator.pl`. It ran on Azure Container
  Apps until 2026-07-12, when the subscription died — ADR-0034.
- There are **no production users yet**. This matters: breaking changes are still cheap, and
  several cost/risk decisions (Part 11) are made for a zero-user world on purpose.

### 1.4 One inspiration and one reference

- A **Russian community** translated LOTRO years ago with the same core technique (same native
  DLL, same fragment identity). Their experience (documented in `docs/RUSSIAN_PROJECT_RESEARCH.md`)
  confirmed the approach and warned about over-engineering.
- The TMS architecture is **copied deliberately from a proven reference project**
  (*TheKittySaver*, by the same author): vertical slices, domain-driven design, the Result
  pattern, the self-hosted auth server, testing style. One rule changed repo-wide: **no mediator
  library** (ADR-0001). Copying a proven skeleton saved months; changing one thing consciously
  kept the design honest.

---

## Part 2 — The domain, in plain words

Read this part slowly. Everything else in the system exists to serve these ideas.

### 2.1 The core vocabulary

| Term | Plain meaning |
|---|---|
| **Fragment** | One piece of game text. Example: one quest dialog line. The game has ~790,000 of them. |
| **FileId + GossipId** | The fragment's identity. `FileId` says which internal game file holds it; `GossipId` is the fragment's number inside that file. Together they form the **FragmentKey**. This pair is **stable across game updates** — proven by real experiments. The text may change; the key does not. |
| **Source text** | The English text of a fragment, plus its argument metadata. This is what translators translate *from*. |
| **Translation** | Our Polish text for one fragment. In the database: **one row per fragment, forever** — the row is updated in place, never duplicated per version. |
| **Placeholder** | The marker `<--DO_NOT_TOUCH!-->` inside a text. The game inserts a value there at runtime (a player name, a number). Translators must keep every placeholder. |
| **Args order** | Polish grammar sometimes needs the inserted values in a different order than English. This field (for example `2-1`) tells the patcher how to reorder them. |
| **Game version** | One release of the game, named the way LOTRO names them: `48`, `48.7`, `47.1.1`. Every import of texts is tied to a game version. |
| **Import** | The admin uploads a fresh `exported.txt` (all English texts) for a new game version. The system compares it with what it has (a **diff**) and updates the catalog. |
| **Invalidation** | When a fragment's English changes, our existing Polish for it becomes suspicious. The system marks it **NeedsReview**. That is invalidation — nothing is deleted. |
| **Approval** | An admin reviews a translation and marks it **Approved**. Only approved translations reach players. |
| **The artifact** (distributed file) | One pre-built text file with all approved translations. Players' patchers download it. It is rebuilt in the background after changes. |
| **Translator** | A person with an account. The TMS keeps a small local profile (display name, email) so lists can show *who* translated *what*. |

### 2.2 The life of one translation (the state machine)

Every translation row is always in exactly **one** of four states:

```
                 ProvideTranslation                Approve
  Untranslated ────────────────────►  Draft  ─────────────────►  Approved
       ▲                                ▲                            │
       │                                │ ProvideTranslation         │  English source
       │ (new fragment                  │ (translator fixes it)      │  changes in a
       │  arrives via import)           │                            │  game update
       │                                │                            ▼
       └────────────                NeedsReview  ◄───────────────────┘
                                    (invalidated — needs a human look)
```

- **Untranslated** — the fragment exists, no Polish yet.
- **Draft** — someone wrote Polish, but it is not reviewed yet. *Editing an Approved row also
  moves it back to Draft* — an edit un-publishes the row until someone approves it again.
- **Approved** — reviewed and published. Only these rows go into the distributed file.
- **NeedsReview** — the English changed after the Polish was written. A human must re-check.

Three important design choices hide in this picture:

1. **One status field, no extra flags.** There is no separate "invalidated" boolean. If there
   were, you could have an impossible combination like "Approved and invalidated at the same
   time". With one enum, illegal states simply cannot be represented.
2. **Invalidation keeps the old work.** The stale Polish stays in the row as a draft starting
   point, and the *old English* is saved in a field called `PreviousSourceText`. The editor can
   then show: old English → new English → current Polish, side by side. `PreviousSourceText`
   is **frozen at the first invalidation** — if the English changes three more times before a
   human looks at it, the field still holds the English that the current Polish was written
   against. Approving clears the field.
3. **"Fallback to English" by exclusion.** An invalidated row is simply **left out** of the
   distributed file. We never copy English into the Polish field. The player's game shows the
   fresh English text naturally, because the patcher never touched that fragment.

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
- **Processed** — an import ran against it. Processing is **idempotent**: importing the same
  file again for the same version is safe and changes nothing (idempotent = running it twice has
  the same effect as running it once).
- **Superseded** — a *newer* version was processed while this one was still waiting, so this one
  will never be processed. Example: versions 48.1 and 48.2 both wait; the admin imports 48.2;
  48.1 becomes Superseded automatically.

Forbidden moves are enforced in code: a Processed version can never become Superseded, and a
Superseded version can never be Processed. A Processed version can never be deleted either — it is
the one an import was applied against. An Unprocessed or Superseded version that no translation
points to may be deleted (for fixing typos in manually registered versions). Superseded counts here
because it was registered and then skipped: no import ever landed against it, so nothing references
it, and refusing to delete it used to burn that version number forever (#624).

Game version names are **canonical**: `48`, `48.0` and `48.0.0` are the same version. The system
removes meaningless trailing zeros before storing or comparing (ADR-0003). Without this rule,
duplicate checks would silently fail.

**How do new versions appear?** The plan from the spec was a "forum watcher" that reads the
official LOTRO release-notes forum (the forum title is the only reliable version signal — the
DAT file's internal version number never changes; see Part 12, question 17). The watcher is
**deliberately postponed** (post-MVP). Today an admin registers a version manually on the
`/game-versions` page — and producing the fresh export stays a manual, now-unelevated ceremony
too (the unattended-VM idea was considered and deferred with written revisit triggers, ADR-0030).

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
   - **Unchanged** — nothing to do. Truly nothing: the row is not written, its `UpdatedAt`
     timestamp does not move.
6. The version becomes Processed; older waiting versions become Superseded.
7. The distributed file is rebuilt in the background. Invalidated rows are excluded, so no
   player ever sees Polish that no longer matches the game.
8. Translators see the NeedsReview queue and work through it; admins approve; the file grows
   again.

Two safety rules guard step 5:

- **All-or-nothing parsing.** If even one line of the upload cannot be parsed, the whole import
  is rejected. Why: a silently skipped line is indistinguishable from a removed fragment, and
  "removed" has consequences (soft removal). A truncated or corrupted file must fail loudly.
- **The mass-removal guard.** If the diff wants to remove more than **20%** of the known
  fragments, the import is rejected unless the admin explicitly passes an override flag. A
  half-file upload would otherwise soft-remove half the catalog. (On the very first import into
  an empty catalog the guard is defined as 0% — an empty catalog cannot "lose" anything.)

### 2.5 The `||` file — the contract between the two worlds

Both directions of data travel use one plain-text format. One line per fragment:

```
# comment lines start with a hash; empty lines are ignored
file_id||gossip_id||content||args_order||args_id||approved||source_digest
620756992||1001||Witaj w Śródziemiu!||NULL||NULL||1||3f9a1c0e7b2d4a55
620756992||1002||Masz <--DO_NOT_TOUCH!--> złota i <--DO_NOT_TOUCH!--> srebra.||2-1||2-1||1||9c02e4d1a7f0b366
```

Rules worth knowing (they all exist for a reason):

- **`source_digest` says which English the row belongs to (ADR-0047).** It is 16 hex characters of a
  hash over the row's source text and its two argument columns. The patcher writes a fragment only
  when the DAT still holds exactly that English, or what the patcher itself last wrote there — so
  between a game update and the next import, a translation made for the old wording is skipped and
  the player sees the game's own new text. Both readers still accept the older six-column form (an
  older export, a hand-made file); both writers always emit seven, and a row without the column is
  reported and never written into the DAT.

- **The content itself may contain `||`.** Parsers on both sides read the first two fields from
  the front, the last three fields from the back, and join everything in the middle back
  together as the content. Naive `split("||")` would corrupt such lines.
- **Content is escaped in the file, raw everywhere else (ADR-0039).** A real line break becomes
  `\r` / `\n` and a real backslash becomes `\\`, so one row is always one line and the transform is
  reversible. **Every writer escapes and every reader unescapes** — the patcher's exporter and
  parser, the TMS' serializer and import parser. What the database holds, what the editor shows and
  what lands in the DAT is always the raw text; the escape exists only between them. (Before 2026-08
  only the patcher's exporter escaped and only its parser unescaped, which is why a translation typed
  with a line break used to vanish from the file — #596.)
- **`args_order`**: `NULL` means no arguments. `2-1` means "swap the two inserted values"
  (1-indexed in the file; the patcher converts to 0-indexed internally).
- **`approved`**: the patcher patches only rows with `1`. The export always writes `1`; the TMS
  **ignores** the column on import (approval is owned by the TMS review workflow, not by a text
  file) and always writes `1` on export (it only exports approved rows anyway).
- The TMS serializer writes **CRLF** line endings and sorts rows by `(FileId, GossipId)` — so
  the same data always produces byte-identical output, which makes the file's content hash (used
  as the HTTP ETag, Part 5.7) stable and the patcher's DAT writes sequential.
- The TMS import reads **strict UTF-8** — one invalid byte rejects the upload (part of the
  truncation guard).

**Changing this format requires an ADR plus updated "golden" test files on both sides.** The
format is the one thing both worlds depend on, so it is protected by process and by tests
(`ParserContractParityTests`, `TranslationFileSerializerParityTests` — the TMS output must
survive a round trip through the *patcher's* parser byte-identically).

### 2.6 People and permissions

Two roles exist (defined once, in `SharedKernel/Authorization/AuthConstants.cs`):

- **Translator** — can browse everything, use the editor, and save drafts (`upsert`). Every new
  self-registered account gets this role.
- **Admin** — everything a Translator can, plus: approve translations (single and bulk), import
  export files, register and delete game versions. The admin account is seeded from
  configuration at startup.

Two endpoints are deliberately public (no login): the **translation list** (read-only browsing
— it markets the project) and the **translation-file download** (players' patchers have no
accounts). Everything else requires a login by default — an endpoint must *opt out* to be
public, never the other way round.

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
    DomainErrors.GameVersionEntity.VersionAlreadyRegistered);

if (r2.IsFailure)
{
    // r2.Error has a Code, a Message, and a Type
}
```

An `Error` carries three things: a machine-readable **Code** (like
`TranslationEntity.CannotApproveWithoutTranslation`), a human **Message**, and a **Type** that
the API layer later maps to an HTTP status:

| Error type | Meaning | HTTP status |
|---|---|---|
| `Validation` | The input is wrong | 400 |
| `NotFound` | The thing does not exist | 404 |
| `Forbidden` | You may not do this | 403 |
| `DataConflict` | The action conflicts with current state | 422 |
| `Failure` | Everything else | 500 |

The `Result` type protects itself: a success cannot carry an error, a failure must carry one,
and reading `.Value` of a failed result throws immediately (that would be a programming bug, not
a business situation).

**Exceptions still exist — for programmer errors only.** Passing `null` where null is
impossible, an empty ID, an unset enum — these throw at once (via guard helpers in
`Ensure.*` or `ArgumentNullException.ThrowIfNull`). The line is simple: *a user or the data can
cause a `Result.Failure`; only a developer mistake can cause an exception.* The API has
exception handlers, but they are a safety net, not a control-flow mechanism.

There is also `Maybe<T>`: a container that either holds a value or is empty. Repositories return
`Maybe<Translation>` instead of `null` for "not found", so the compiler forces every caller to
handle the missing case.

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
- The **two factory methods are a security idea**: input from the outside world goes through
  `Create(Guid)` (validated — an empty GUID is rejected), while values read back from our own
  database go through `FromValue` (the store is trusted; re-validating would be noise).

These are **hand-written**, not generated by a library. The project first used a popular
source-generator package, then dropped it (ADR-0010): the package was unmaintained and its
generated code had a public constructor that skipped validation. The IDs are ~30 lines each —
small enough to own.

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
| `LotroNotationVersion` | version string | max 12 chars, digits-and-dots grammar, canonical form (`48.0` → `48`) |
| `FragmentKey` | FileId + GossipId | `FileId > 0`, `GossipId >= 0` (zero is legal! — the patcher parser allows it, so the TMS must too) |
| `TranslationSource` | English text + ArgsOrder + ArgsId | normalizes `"NULL"`/blank args to real `null`; **equality covers all three fields**, so a change in argument structure counts as a meaning change even if the text is identical |
| `DisplayName` | translator's name | trimmed, non-empty, max 150 |
| `Email` | translator's email | trimmed, simple format check, max 250 |

Two details worth quoting in an interview:

- **Validation order is fixed**: empty check → trim → length → format. So an all-spaces input
  fails as "empty", never as "wrong format" — consistent error codes everywhere.
- Value objects compare **by content** (the base class compares a list of "atomic values"), not
  by reference. Two `FragmentKey(1, 5)` instances are equal. Entities, in contrast, compare by ID.

### 3.4 Entities and aggregates

- An **entity** has an identity (an ID) and a life story. Two entities are the same thing if
  their IDs match, even when their data differs.
- An **aggregate** is a small cluster of data with **one guard at the door** (the *aggregate
  root*). All changes go through methods on the root, and those methods enforce the rules. You
  never set a property from the outside; there are no public setters.

The TMS has exactly **three aggregates** — deliberately small ones:

1. **`GameVersion`** — the version state machine from Part 2.3. Methods: `MarkAsProcessed()`,
   `MarkSuperseded()`, `EnsureCanBeDeleted()` — each returns `Result` and refuses illegal moves.
2. **`Translation`** — the rich one; the state machine from Part 2.2. Methods:
   `CreateUntranslated(...)` (factory), `ApplySourceChange(...)`, `MarkRemoved(...)`,
   `Restore(...)`, `ProvideTranslation(...)`, `Approve(...)`. The `Approve` method carries the
   real guards ("no Polish text → fail", "removed → fail") and returns `Result`.
3. **`Translator`** — a lean local profile of an auth user: `IdentityId`, `DisplayName`,
   `Email?`, plus `RefreshProfile(...)` so a renamed account converges on next contact.

Rules that keep the model clean:

- **Aggregates reference each other only by ID.** A `Translation` stores a `GameVersionId` and
  `TranslatorId?`s — never an object reference to another aggregate. No accidental object webs.
- **Not everything is an aggregate.** The pre-built distribution file
  (`PrecomputedTranslationFile`) guards no business rule — it is derived data, fully
  regenerable from translations. It is modeled as a plain projection with its own small store
  interface, *not* an aggregate with a repository (ADR-0007). Putting it in the domain would be
  a category error.
- Class layout follows one fixed order (constants → fields → properties → behavior methods →
  static factory → private constructors), same as the reference project, so every aggregate file
  reads the same way.

### 3.5 Repositories and the Unit of Work

A **repository** is the door to stored aggregates: `GetByIdAsync` (returns `Maybe<T>`),
`ExistsAsync`, `Insert`, `Remove`, plus a few purpose-built methods per aggregate (for example
`ITranslationRepository.GetByFragmentKeyAsync`, or `AnyReferencesGameVersionAsync`, which backs
the "may I delete this version?" check).

The **Unit of Work** (`IUnitOfWork`) is the "commit button". Handlers change aggregates in
memory, then call `SaveChangesAsync` once — one transaction, all-or-nothing. The import flow
uses two special helpers on it (explained in Part 5.4): `ExecuteInTransactionAsync` (a
transaction that survives transient network faults safely) and `SaveChangesAndClearAsync`
(save one chunk, forget it, keep memory flat).

### 3.6 Commands, queries and handlers — CQRS without a mediator

Every use case in the system is either a **command** (changes state) or a **query** (reads
state). Each is a small record, handled by exactly one handler class:

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
MediatR** and no mediator of any kind (ADR-0001, the project's oldest and most defining
decision). Instead of `sender.Send(command)` going through a magic pipeline, the endpoint asks
dependency injection for the **exact handler interface** and calls it:

```csharp
// DI registration — one explicit line per use case:
services.AddScoped<ICommandHandler<ApproveTranslation.Command, Result>, ApproveTranslation.Handler>();

// The endpoint takes the closed interface as a parameter and just calls it:
ICommandHandler<ApproveTranslation.Command, Result> handler ... 
Result result = await handler.Handle(command, cancellationToken);
```

What the mediator would have given us, we do explicitly instead:

- **Validation**: command handlers inject `IValidator<TCommand>` (FluentValidation) and turn
  failures into `Result.Failure` — never a thrown exception. Queries validate inline in the
  handler (house rule: validators exist *for commands only*).
- **Logging**: a plain `ILogger<Handler>` inside the handler.
- **Wiring safety**: a missing registration fails at startup/DI-resolution, and "find all
  usages" in the IDE actually works, because the call is a normal interface call.

Why this is better *here*: the mediator's indirection served nobody (no cross-cutting pipeline
was actually used), it hid the wiring until runtime, and it pulled in a transitively vulnerable
package. Full argument: Part 12, question 2.

### 3.7 The read side: read models

The system separates **writing** from **reading** (this separation is called CQRS):

- **Writes** load an aggregate through a repository, call a behavior method, save via the Unit
  of Work. The aggregate enforces the rules.
- **Reads** never touch aggregates. They query **read models** — plain records with public
  properties and zero behavior — through a read-only database context
  (`IApplicationReadDbContext`, no change tracking).

Both sides map to the **same physical tables** (no data duplication, no synchronization): the
write model maps `Translation` with its value objects onto the `Translations` table; the read
model `TranslationReadModel` maps the same table as flat columns, plus navigation joins to show
submitter/approver names. The read side can shape data freely for lists and filters without
ever being able to break a business rule — it physically has no methods to call.

### 3.8 The one projection: the pre-built translation file

`PrecomputedTranslationFile` (project `TranslationSystem.Projections`) is a single row per
language holding: the full serialized `||` file content, its SHA-256 content hash, and a
timestamp. It is written through a minimal store port (`IPrecomputedTranslationFileStore`) with
a set-based update that never loads the old multi-megabyte content into memory. Who rebuilds it
and when — Part 5.6.

### 3.9 The diff engine — a pure domain service

The import comparison logic (`TranslationDiffService`) is a **static, pure function**: stored
catalog in, uploaded file in, **plan** out (`TranslationDiffPlan`: which keys are added, which
IDs changed source, which are removed/restored, plus counters). It touches no database and has
no side effects — which makes it trivially unit-testable.

Because the catalog is ~790k rows, the service is built to stay **small in memory**:

- The stored side streams through as compact value structs (`StoredSourceDigest`: ID, key,
  hash, status, removed-flag) — never full aggregate objects, never all rows at once.
- Source texts are compared by a **128-bit hash** (`SourceHash`, SHA-256 cut to 128 bits) of
  the triple *(text, args order, args id)* — with length framing so `("ab","c")` and
  `("a","bc")` hash differently. Chance of a false "unchanged" at this scale: about 10⁻²⁶.
  The hash is computed on the fly and never stored.
- The plan's size grows with the **diff**, not with the file: a typical update touches a few
  thousand rows out of 790k.

This design has a story behind it (the import once crashed the server by running out of
memory) — told in Part 5.4 and Part 12, question 13.

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

  Utilities/                        small shared helpers (HATEOAS link types, logging redaction)
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
patcher. The frontend talks to the API over HTTP using the `Contracts` DTOs.

### 4.2 One use case = one file (vertical slices)

The API is organized by **feature**, not by technical layer. Every use case is one file under
`TranslationSystem.API/Features/<Area>/<Action>.cs`, containing three nested pieces:

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
        app.MapPost("api/v1/translations/{id:guid}/approve", ...)
           .RequireAuthorization(AuthorizationPolicies.RequireAdminRole);
    }
}
```

All `IEndpoint` classes are found by an assembly scan at startup and mapped automatically.
Adding a feature means: add one file, add one DI line for the handler, add DTOs to `Contracts`,
add tests. Deleting a feature means deleting one file. Nothing else changes — that is the point
of vertical slices: **change stays local**.

### 4.3 The runtime picture

Three applications and one database run together. In development they all run on your machine:

```
                 browser (translator)
                        │ HTTPS
                        ▼
   ┌–– login ––► auth-api :5003 ◄––– validates cookies, issues tokens (OpenIddict)
   │                    ▲
   │                    │ back-channel: code→token, JWKS (public keys)
   ▼                    │
 frontend :7017 ────────┘
   (Blazor SSR)─────────► tms-api :5002 ──► PostgreSQL :5432 (2 databases:
        bearer token          ▲                lotro_translation + lotro_auth)
                              │
   patcher CLI (player) ──────┘  GET /api/v1/translation-files/pl  (anonymous, ETag-cached)
```

- **auth-api** — the identity provider. Login/register pages, token endpoint, user database.
- **tms-api** — the domain API. Validates tokens issued by auth-api (it never sees passwords).
- **frontend** — server-rendered UI. Holds the user's session cookie; calls tms-api with the
  user's access token; never talks to the database.
- **the patcher CLI** — anonymous client of exactly one endpoint (the file download).

Supporting services in development: **mailpit** (a fake SMTP server with a web inbox at :8025 —
catches confirmation emails) and the **Aspire dashboard** (:18888 — shows logs and traces).

---

## Part 5 — The main flows, end to end

This part walks through every important scenario step by step. If you understand these six
flows, you understand the system.

### 5.1 Login (OIDC authorization code + PKCE)

The system uses the standard web login flow. In plain words:

1. You click **Zaloguj** on the frontend. The frontend redirects your browser to
   auth-api's `/connect/authorize`, adding its client ID, a redirect address, and a **PKCE
   challenge** (a one-time secret hash that proves later that the same app finishes the flow —
   protection against stolen redirect codes).
2. auth-api shows its **login page**. You enter **email + password** (email is the login
   identifier; the username is only a display handle — ADR-0022).
3. auth-api checks the password (with brute-force protections — Part 7.4), then redirects your
   browser back to the frontend with a short-lived, one-time **authorization code**.
4. The frontend's server exchanges that code (plus the PKCE secret) at `/connect/token` for:
   - an **access token** — a signed JWT, valid **60 minutes**; it carries `sub` (your account
     ID), `name`, `email`, and `role`;
   - a **refresh token** — a *reference* token, valid **14 days**, stored server-side in the
     auth database, so it can be revoked; it is *rolling*: every use replaces it.
5. The frontend stores the tokens **inside an encrypted cookie** (`.lotrokoniecdev.auth`,
   HttpOnly, 8-hour sliding). The browser never sees raw tokens in JavaScript — there is no
   JavaScript.
6. On every later request, a cookie validator (`CookieTokenRefresher`) silently keeps the
   session healthy: if the access token is near expiry, it uses the refresh token to get a new
   one; it also re-checks the token's signature against auth-api's published public keys, and it
   rejects sessions that a previous 401 marked as dead. When nothing can be saved, you are
   signed out cleanly and see a one-time "session expired" note.

If auth-api is down, the login button leads to a friendly **503 "login unavailable"** page (the
frontend probes the auth discovery document first), instead of an ugly error.

### 5.2 Lazy provisioning — how a Translator profile appears

The auth system and the TMS are separate worlds with separate databases. When you register, the
auth system does **not** call the TMS to create a profile (no cross-system transaction, nothing
to break). Instead (ADR-0004):

- On the **first authenticated request** to tms-api, a middleware (plus, authoritatively, every
  write handler) calls the **provisioner**: *"find a Translator row for this `sub`; if there is
  none, create it from the token's name/email claims."*
- The operation is **idempotent**: a unique database index on `IdentityId` guarantees one row
  per account even if two first requests race — the loser of the race detaches its insert,
  re-reads the winner's row, and continues.
- A small in-process cache (5-minute TTL, keyed by a hash of the name+email claims) makes the
  steady state free: no database query per request. If your claims change (account renamed),
  the fingerprint differs and the row is refreshed.

Why it matters: the translation list must show *other people's* names ("submitted by X,
approved by Y"). Your token proves who *you* are, but cannot resolve other users — so the TMS
keeps its own tiny, always-converging copy of each user's public profile.

### 5.3 Registering a game version

1. Admin opens `/game-versions` on the frontend. The page shows every known version, newest
   first, with its status.
2. The register form is visible **only if the API said so**: the collection response carries a
   `register` link only for admins (this link-driven UI idea is explained in Part 5.8).
3. Submitting calls `POST /api/v1/game-versions`. The handler validates the version text,
   canonicalizes it (`48.0` → `48`), rejects duplicates **after** canonicalization, and stores
   a new `Unprocessed` version.
4. A mistyped version can be deleted as long as it is not `Processed` **and** no translation row
   references it. That includes a `Superseded` row — one the admin registered by mistake and then
   skipped by importing the real version — which is how its version number is freed for the real
   update when it eventually ships (#624). Both checks are server-side; the frontend's delete button
   is, again, only rendered when the API advertises a `delete` link.

### 5.4 The import — uploading a fresh export

The admin uploads `exported.txt` (~80 MB, ~790k lines) on `/import-export`, choosing the game
version. What happens inside `POST /api/v1/game-versions/{id}/import`:

**Pass 1 — plan (read everything, write nothing).**
The handler streams the uploaded file line by line, validating each row and building a compact
map *key → source-hash*. In parallel terms it then streams the current catalog from the
database — as small value structs, deliberately **not** through EF Core's normal query path
(EF's retry logic buffers whole result sets; with 792k rows that buffer once killed the
container — see the story below). The pure diff service compares both sides and produces the
plan: added / source-changed / removed / restored / unchanged.

Then the guards run, in order: any parse error → reject everything (422); removed fraction over
20% without the override flag → reject (`Import.MassRemovalBlocked`); version is Superseded →
reject. Nothing has been written yet.

**Pass 2 — apply (one transaction).**
Inside a single database transaction:
- **Added rows** stream straight into PostgreSQL's native bulk-load channel (**binary COPY**) —
  the fastest possible insert path; the rows never exist as a full in-memory list.
- **Source-changed / removed / restored rows** are loaded in chunks of 5,000, mutated through
  the normal aggregate methods (`ApplySourceChange`, `MarkRemoved`, `Restore` — so every
  domain rule from Part 2 applies), saved, and released from memory before the next chunk.
- The version is marked **Processed**, and older still-Unprocessed versions are marked
  **Superseded** — in the same transaction as the data changes.

**After commit:** the artifact rebuild is *scheduled* (not executed inline — Part 5.6), and the
admin gets an `ImportSummary`: added / sourceChanged / invalidated / removed / unchanged counts.

Details that make this flow production-grade:

- **Re-upload is idempotent.** Importing the same file again produces "unchanged" for every row
  and writes nothing — tests assert that even the `UpdatedAt` timestamps stay frozen.
- **Transient-fault safety.** The database connection retries on flaky networks. A subtle trap:
  if the commit succeeds but the *acknowledgement* is lost, a naive retry would re-run with an
  empty change set and silently drop work. The Unit of Work therefore defers "accept changes"
  until the commit is confirmed (`acceptAllChangesOnSuccess: false` + explicit accept), and the
  whole Pass 2 is written to be safely re-runnable.
- **The upload cap is 256 MB** (config-adjustable), about 3× today's file — raised deliberately
  on *both* hops (frontend and API), with a clean 413 answer beyond it (spec 0003).
- **The memory story (spec 0006):** the first real import crashed the production container
  (0.25 vCPU / 0.5 GB) with an out-of-memory kill, because four full copies of the file's data
  were alive at once. The fix was this two-pass streaming design: nothing may materialize the
  whole file or the whole catalog. The temporary "just give it 4 GB" bridge was reverted the
  same week — the correct fix was cheaper than the workaround.

Performance today: a full baseline import (~790k rows) completes **in seconds** (the test
budget is < 10 s against a real PostgreSQL; before specs 0004/0006 it took ~3 minutes).

### 5.5 Translating and approving

**Browsing.** `GET /api/v1/translations` — the public, paginated list. Filters: text search
(case-insensitive, over both English and Polish, with SQL wildcard characters escaped so a
literal `%` in game text matches literally), status filter, page size 1–100 (clamped, default
50). Sorting always ends with a unique tiebreaker so pages never repeat or lose rows. Search is
fast because of trigram indexes (Part 6.2).

**Editing.** The editor page loads one row (`GET /api/v1/translations/{id}` — this one needs a
login) and shows English and Polish side by side, with every `<--DO_NOT_TOUCH!-->` placeholder
highlighted. For a NeedsReview row it also shows `PreviousSourceText` — the English the current
Polish was written against. Saving posts `PUT /api/v1/translations` with
`(FileId, GossipId, TranslatedText)`. The handler:

1. loads the row by fragment key — unknown key → 404 (rows are *born from imports*, never from
   the editor; an upsert cannot invent a fragment the game does not have);
2. rejects a soft-removed row (422 `CannotEditRemoved`);
3. provisions the caller's Translator profile (Part 5.2) and stamps `SubmittedById`;
4. calls `ProvideTranslation(...)` → the row becomes **Draft** (whatever it was before — editing
   an Approved row un-publishes it);
5. if the row *was* Approved, schedules an artifact rebuild (the published file must lose the
   now-unreviewed row).

The placeholder check on the frontend is **advisory**: if the Polish has a different number of
placeholders than the English, the editor shows a warning but still lets you save (game text is
strange sometimes; the human decides). The API stores the text exactly as typed.

**Approving.** `POST /api/v1/translations/{id}/approve` (admin only) calls the aggregate's
`Approve(...)`: fails without Polish text, fails on a removed row; otherwise sets **Approved**,
stamps `ApprovedById`, clears `PreviousSourceText`, returns 204, schedules a rebuild.

**Bulk approving (spec 0007).** The list page lets an admin tick checkboxes and approve up to
**100 rows** (one page) in one `POST /api/v1/translations/approve`. The semantics are
**best-effort**: every row that is still approvable (Draft or NeedsReview, not removed) is
approved; the rest are silently counted as skipped — one stale row must never block the batch.
Response: `{requested, approved, skipped}`. One transaction, one rebuild scheduled, and only if
at least one row was actually approved.

**Two people at once (optimistic concurrency).** Translation rows carry PostgreSQL's built-in
row-version column (`xmin`) as a concurrency token. If translator A and admin B load the same
row and both write, the second write detects that the row changed under it and fails with
**409 Conflict** instead of silently overwriting the first change. This closed a real audit
finding: an approve racing an import could previously overwrite the import's invalidation —
publishing stale Polish. The token is a *shadow* property: the domain class knows nothing about
it; EF maps it from the system column, so the migration adding it changed no physical schema.

### 5.6 Rebuilding the distributed file (background, debounced)

Every write that changes what players should get (import, approve, bulk approve, editing an
approved row) does **not** rebuild the file inline. It drops a message into an in-process
queue and returns. A background worker:

1. waits a short **debounce window** (default 2 seconds) and collects everything that arrived —
   an admin clicking approve 20 times in a row causes **one** rebuild, not 20;
2. queries the read model for all **Approved, not-removed** rows, ordered by
   `(FileId, GossipId)`;
3. serializes them into the `||` format (CRLF, `approved=1`), computes the SHA-256 content
   hash, and updates the single artifact row with a set-based UPDATE (never loading the old
   multi-MB content);
4. on failure, logs and **re-schedules itself** — the file always converges to correct.

This replaced an earlier inline design where every approve paid a full O(N) scan + serialize +
hash under a process-wide lock (ADR-0021). Trade-offs accepted consciously: the file is now
*eventually consistent* (a download a second after an approve may be one debounce window
behind — harmless for this product), and the queue is in-process, which assumes a **single API
replica** (a documented blocker to revisit before scaling out).

### 5.7 Distribution — how players get the file

`GET /api/v1/translation-files/pl` — anonymous, and deliberately boring:

- The response is the pre-built file with an **ETag** header equal to the content hash.
- The patcher stores the ETag in a sidecar file next to its cached copy. Next time it sends
  `If-None-Match: <etag>`; if nothing changed, the server answers **304 Not Modified** with no
  body — the check costs almost nothing on both sides. The 304 decision reads only the hash
  column from the database; the multi-MB content column is fetched only on a real miss.
- The endpoint does **zero work per request** — no query over translations, no serialization.
  A thousand players hitting it on patch day read one small row. This is the anti-stampede
  design from spec 0001.

**The player's `launch` flow** ties it together: sync the file from the TMS (**304 → use
cache; 200 → save new file + ETag; network down → use the cached file and continue** — a launch
is never blocked by the server); refuse to run if the game is already open; hash the local
translation file and compare with the last patched hash; **re-patch only if it changed**; start
the game's official launcher and exit. Two hardening rules on the sync (AUDIT-SEC-01): the TMS
URL must be **HTTPS** (plain HTTP only for loopback — the file is about to be written into the
game), and the downloaded content's SHA-256 must **match the ETag** or the download is rejected
and the cached file is used. (Today the CLI's default TMS URL is empty until the public URL is
considered stable, so sync runs when `--tms-url` is passed.)

The frontend also re-serves the same artifact at `/download/polish.txt` for humans who want the
file from the website.

### 5.8 HATEOAS — the API tells the UI what is allowed

Every read endpoint can include a `links` array: *self*, plus **only the actions the caller may
perform on that resource in its current state**. Examples: a translation row carries `upsert`
for translators, plus `approve` only if the caller is an admin *and* the row is Draft or
NeedsReview; the versions list carries `register` only for admins; a soft-removed row carries
only `self`.

The frontend renders buttons **only for links the API actually sent** — it never re-computes
"am I an admin?" locally. One brain (the server) decides permissions; the UI cannot drift from
it. Links are opt-in via a vendor media type in the `Accept` header
(`application/vnd.dev-lotrokoniecdev.hateoas.json`); plain `application/json` responses carry
no links, so ordinary clients see a clean payload. Command responses carry no links — you
mutate, then re-GET and see your new options.

---

## Part 6 — The database

### 6.1 Two databases, one server

- **`lotro_translation`** — the TMS. Schema `translation`, four tables:
  `Translations` (~790k rows), `GameVersions`, `Translators`, `TranslationArtifacts` (one row
  per language — the pre-built file).
- **`lotro_auth`** — the auth system: ASP.NET Identity tables (users, roles) plus OpenIddict
  tables (clients, authorizations, reference tokens).

Two databases because the two systems are separate worlds — separate migrations, separate
lifecycles, one server instance for cost. In production both live in one **Neon** project
(serverless PostgreSQL 18; a point-in-time-restore rewind restores *both together*, which is
correct because one migrator run migrates both).

### 6.2 Mapping choices worth knowing

- **Value objects** map with `ComplexProperty` by default (the semantically correct mapping —
  pure value, no identity). The exception: a VO that needs a **database index** maps as
  `OwnsOne`, because EF Core 10 cannot index complex properties. That is why `FragmentKey`
  (unique index), `TranslationSource` (search index) and `LotroNotationVersion` (unique index)
  are owned, while `DisplayName`/`Email` are complex properties.
- **Enums are stored as strings** (`Status = 'Approved'`) — readable in any SQL console, safe
  against renumbering.
- **All timestamps are `timestamptz` normalized to UTC** by a global converter.
- **Indexes:** unique `(FileId, GossipId)`; unique version; unique `IdentityId` on Translators
  (the provisioning idempotency key); unique `Language` on artifacts; **trigram GIN indexes**
  (PostgreSQL `pg_trgm` extension) on both `SourceText` and `TranslatedText` — these make the
  `%term%` contains-search fast, which a normal B-tree index cannot do; a partial index on
  `Status` filtered to non-removed rows (the only rows lists ever show); and real foreign keys
  (with their indexes) on the three GameVersion pointer columns of `Translations`
  (`IntroducedInVersion` / `LastSourceChangeInVersion` / `RemovedInVersion` — AUDIT-EF-05).
- **`xmin` concurrency token** — see Part 5.5; zero physical schema cost.

### 6.3 Migrations — forward-only, N-1 compatible

Rules (ADR-0023, proven in CI by ADR-0024):

- **Migrations only ever roll forward.** `Down()` is never run outside a local sandbox. In a
  shared environment, undoing a migration while the app already wrote data is how you corrupt a
  database. Recovery is: fix forward, or restore the database to a point in time.
- **Every migration must keep the *previous* app release working** ("N-1 compatible"). Reason:
  during a deployment, the migration runs *first*, while the old code still serves traffic; and
  if the new code fails its health checks, the rollback re-activates the old code **on the new
  schema**. So the old code must survive the new schema, always.
- Destructive changes (drop/rename a column, add NOT NULL, …) therefore ship as
  **expand → backfill → contract** across at least two releases: first add the new thing next
  to the old, migrate the data, teach the code to use the new thing; only in a later release
  remove the old thing.
- **Enforcement is layered:** a CI text-scan flags destructive statements in new migrations
  (override = an explicit in-file `MIGRATION-SAFETY: acknowledged — <reason>` comment), and a
  dedicated CI job on migration PRs actually **runs the previous release's integration tests
  against the new schema** — an executable proof, not a promise.
- Migrations run as a **one-shot migrator job before rollout** — never at app startup (N app
  replicas racing to migrate is a classic failure). The deploy is blocked until the migrator
  succeeds; right before it runs, a Neon branch snapshot is taken as a durable restore point.

---

## Part 7 — Authentication and authorization, in depth

### 7.1 The three-actor model

Standard OAuth2/OIDC roles, self-hosted:

- **auth-api** is the *authorization server* (OpenIddict on top of ASP.NET Identity). It owns
  passwords, accounts, and token issuing. Nobody else ever sees a password.
- **frontend** is the *client* (relying party) — the cookie + OIDC flow from Part 5.1.
- **tms-api** is the *resource server*. It validates access tokens **offline**: it downloads
  auth-api's public keys (JWKS) once and checks each token's signature, issuer, audience
  (`lotrokoniecdev-api`) and expiry locally. If auth-api goes down, already-issued tokens keep
  working until they expire — the TMS does not fall over with it.

Why self-hosted instead of Auth0/Entra: full control, zero per-user cost, no external
dependency — and the module was lifted from the reference project where it was already built
and tested. The trade-off (you own security updates and key management) was accepted
consciously; Part 12, question 10.

### 7.2 Tokens, keys, sessions

- **Access token**: signed JWT, 60 minutes, **signed but not encrypted** — a deliberate switch
  (`DisableAccessTokenEncryption`) so any standard JwtBearer middleware can validate it.
  Tamper-proof (signature) but readable — never put secrets in claims.
- **Refresh token**: *reference* token — the client holds a random handle; the real state
  lives in the auth database. Revocable (logout revokes all of a user's tokens) and **rolling**
  (each use issues a replacement, so a stolen old one is useless).
- **Keys**: development uses throwaway keys generated at startup. Production uses a real RSA
  signing key (≥2048 bits) and an AES encryption key, injected via environment secrets, with a
  slot for the *previous* signing key so keys can rotate without logging everyone out. A
  documented subtlety: the RSA objects are intentionally never disposed — OpenIddict needs them
  alive for the whole process to serve JWKS.
- **Session cookies**: the auth server's own login cookie is strict (HttpOnly, SameSite=Strict,
  30-minute sliding) and re-validates the user's **security stamp on every request** — so a
  password change or account deletion kills every live session immediately, and a stolen cookie
  cannot quietly mint new tokens.
- **Data Protection keyrings** (the keys that encrypt cookies and antiforgery tokens) are
  persisted to a mounted volume in every deployed environment, with a fail-fast startup guard.
  Without this, every redeploy would silently log everyone out (ADR-0005).

### 7.3 Accounts (ADR-0022 and GDPR)

- **Email is the login identifier**; it must be confirmed before the first sign-in, is unique
  at the database level (case-insensitive), and is immutable after registration.
- **Username is a display-only handle**: unique, ASCII letters and digits only, enforced from
  one shared constant in three layers (validator, register page, Identity options). Emails must
  contain `@`, usernames cannot — the two identifier spaces cannot collide.
- Passwords: 8–128 chars with complexity; registration requires all three consents (privacy
  policy, data processing, and — since LEGAL-03 — terms of service); users can export their
  data and delete their account. Deletion is **two-phase** (ADR-0031): the request schedules
  erasure behind a 14-day cancellation window — the account is locked, sessions and tokens are
  revoked, and a one-time cancel link is emailed; only after the window does the background
  finalizer anonymize the account and lock it permanently. Cancelling restores the account but
  forces a password reset, since the deletion request may have come from a stolen password.
- Registration sends a Polish confirmation email (through mailpit in dev). If the email
  *cannot be sent*, the account is auto-confirmed as a fallback — a pragmatic pre-release
  choice: better a user without a confirmed email than a user locked out by an SMTP outage.

### 7.4 Abuse protection

- **Anti-enumeration**: login failures return one generic message; a login attempt for a
  non-existent account still runs a dummy password hash (so timing does not reveal whether the
  email exists); "forgot password" and "resend confirmation" always claim success.
- **Lockout**: 5 failed attempts → 5-minute lock. The email-confirmation check runs *after*
  password verification — order matters, or it becomes a timing oracle.
- **Rate limits by IP**: 20/min general on auth-api; **10/min on `/connect/*` and register**
  (the brute-force surface); 3 per 15 min on forgot-password and resend-confirmation;
  100/min on tms-api. The limiter runs *before* authentication, so junk traffic is counted and
  rejected cheaply. (A real bug lived here once: the `/connect` group was mapped without the
  limiter attached, so the brute-force limit silently never engaged — found by an audit, fixed,
  and covered by a regression test.)
- A daily background job prunes expired reference tokens so the auth tables do not grow forever.

### 7.5 Authorization on the TMS side

- **Default-deny**: the fallback policy demands an authenticated user; public endpoints opt out
  explicitly (`AllowAnonymous`) — the safe direction of mistake.
- Policies: `RequireAdminRole` (role Admin), `RequireTranslatorRole` (Admin **or** Translator).
  Claims stay in their raw OIDC form (`MapInboundClaims = false` on both sides), so `sub`,
  `name` and `role` mean the same thing everywhere.
- `CurrentUserAccessor` reads the caller's identity once per request; write handlers stamp
  `SubmittedById`/`ApprovedById` from it — every content change is attributable to a person.

---

## Part 8 — The frontend

### 8.1 Static SSR — the deliberately boring choice

The frontend is **Blazor Static Server-Side Rendering**: every page is rendered to plain HTML
on the server and sent to the browser. There is **no WebSocket circuit, no WebAssembly, no
JavaScript interactivity, no per-user server state**. Forms are classic HTML `POST`s.

Why: the UI is fundamentally CRUD — lists, filters, an editor form, buttons. Static SSR makes
it fast (nothing to hydrate), cheap (no live connections to hold), simple to reason about
(request in, HTML out), and robust (a browser refresh is always safe thanks to
Post-Redirect-Get). The cost — no rich client-side interactivity — is acceptable for this
product; the one place it shows is "select all pages" style features, which are consciously out
of scope.

This is **enforced, not just documented**: `scripts/check-ssr-purity.sh` runs first in CI and
fails the build if anyone introduces `@rendermode`, interactive `@on*` handlers,
`StateHasChanged`, or an inline `<script>` the Frontend's CSP would block (#670) — `@onsubmit`
and a `<script src=…>` are the allowed exceptions. Wanting interactivity means writing an ADR first.

### 8.2 The pages

| Route | Who | What it does |
|---|---|---|
| `/` | public | Landing page with a progress meter (approved / awaiting / total; served from a 30 s server-side snapshot — AUDIT-EF-04/#354) and download links |
| `/translations` | public | Searchable, filterable, paginated list; per-row Edit link; admin sees bulk-approve checkboxes |
| `/editor/{id}` | logged in | Side-by-side editor: English (and previous English for NeedsReview) vs Polish; placeholder highlighting and mismatch warning; Save + Approve |
| `/dashboard` | logged in | Progress tiles: total / translated / approved / remaining |
| `/game-versions` | logged in | Version list; admin-only register form and guarded per-row delete |
| `/import-export` | logged in | Download `polish.txt`; admin-only upload of `exported.txt` with an import summary |
| `/download/polish.txt` | public | The distributed file re-served for humans |
| error pages | public | Friendly 400 / 403 / 404 / 500 / "login unavailable" (503) pages |

### 8.3 How pages get data and post changes

- A single typed HTTP client (`ITranslationSystemClient`) calls tms-api. It is wrapped in a
  **Polly resilience pipeline**: 2 retries with backoff for safe requests, a circuit breaker,
  and a timeout that understands context — 10 s for JSON calls, **5 minutes for the multipart
  import upload** (an 80 MB file plus a minutes-long import must not be cut at 10 s).
- A delegating handler attaches the user's bearer token and the HATEOAS `Accept` header to
  every call — pages never touch tokens. If a call comes back 401, the handler marks the
  session dead so the next page load signs the user out cleanly instead of looping.
- Results come back as `ApiResult<T>` — the frontend's small Result type carrying
  `ProblemDetails` on failure. Transport failures map to friendly Polish messages ("usługa
  niedostępna", "przekroczono czas") instead of stack traces.
- Forms are plain SSR: `<form method="post" @formname="...">` + antiforgery token + hidden
  fields, bound via `[SupplyParameterFromForm]`. The one `EditForm` (the import upload) emits
  its own antiforgery token — adding a manual one there would double it and break the request
  (a real bug once; now a remembered rule).
- Every successful POST ends in **Post-Redirect-Get** with a query flag
  (`?approved=12`) — refresh-safe, and the redirected GET re-fetches fresh HATEOAS links so
  the buttons always match the new state.
- Page logic that deserves tests (progress math, placeholder analysis, list view models) lives
  in **pure C# classes** next to the `.razor` files, unit-tested without any browser.

---

## Part 9 — Testing

### 9.1 The philosophy

- **Test the behavior, not the implementation.** Tests call the public surface (a handler, an
  endpoint, a parser) and assert what comes out (`Result`, persisted state, HTTP response).
  Mocks are used only for **genuine boundaries** (the DAT file handler, the forum fetcher) —
  never for classes we own. Consequence: a refactor that changes *how* without changing *what*
  breaks zero tests.
- **`.Received()` (did-you-call-it assertions) is almost forbidden** — allowed only for side
  effects invisible in the return value (e.g. "the destructive operation was NOT called when
  validation failed").
- **Unhappy paths are first-class.** Boundary matrices (`[Theory]` + `[InlineData]`): empty,
  too long, malformed, already-in-that-state.
- **AAA (arrange–act–assert), assertions inline**, one reason to fail per test. Tooling: xUnit,
  Shouldly, NSubstitute. Naming: `Method_Scenario_ExpectedResult`.
- **Unit tests are pure** — no filesystem, no network, no database, runnable on macOS and
  Windows alike.

### 9.2 The test pyramid (12 projects)

| Level | Projects | How they run |
|---|---|---|
| Unit — patcher | `Tests.Unit` | pure; handlers with real validators + mocked boundaries |
| Unit — TMS | `SharedKernel.Tests.Unit`, `TranslationSystem.Domain.Tests.Unit`, `TranslationSystem.API.Tests.Unit`, `Frontend.Tests.Unit`, `Logging.Tests.Unit` | pure; fake read-DbContext; bUnit-style component tests for Blazor |
| Integration | `TranslationSystem.API.Tests.Integration`, `AuthSystem.API.Tests.Integration` | the real app in-process (`WebApplicationFactory`) against a **real PostgreSQL in Docker** (Testcontainers); auth is faked with self-signed test tokens |
| End-to-end — TMS | `TranslationSystem.E2E.Tests` | the real container images on a private Docker network; **real tokens** issued by the real auth-api and validated by tms-api via live JWKS |
| End-to-end — browser | `Frontend.E2E.Tests` | full stack + a headless Chromium via Playwright, all inside Testcontainers (ADR-0009): register → confirm email via mailpit → login → logout |
| End-to-end — patcher | `Tests.E2E`, `Tests.Infrastructure` | Windows-only, against a real DAT file; auto-skip elsewhere (`SkippableFact`) |

Also notable:

- **Contract parity tests** (Part 2.5) pin the `||` format across the two bounded contexts.
- **Mutation testing** (Stryker) runs on the Domain and SharedKernel projects in CI: it
  deliberately plants small bugs and fails the build if the test suite does not catch at least
  67% of them — a measure of test *strength*, not just coverage.
- **The N-1 seam**: integration test factories can be pointed at externally-provided schema
  scripts (`N1_COMPAT_SCHEMA_SCRIPTS_DIR`), which is how CI runs *yesterday's tests against
  tomorrow's schema* (Part 6.3). In normal runs the seam is inert.

---

## Part 10 — Running it on your machine

### 10.1 Prerequisites

.NET 10 SDK · Docker · one-time `dotnet dev-certs https --trust` (so the three local apps can
serve HTTPS with the standard ASP.NET development certificate).

### 10.2 The development loop

```bash
scripts/up.sh          # starts INFRA ONLY: postgres, one-shot migrator, mailpit, aspire
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
| http://localhost:18888 | Aspire dashboard — logs and traces |

Why the apps run on the host and only infrastructure runs in Docker (ADR-0006 + amendment):
containers gave neither a fast inner loop (no hot reload, image rebuilds) nor production
parity — and a containerized OIDC client breaks the "one Authority URL for browser and
back-channel" rule that `localhost` satisfies naturally. Dev optimizes for feedback speed; the
parity job belongs to the next stack.

First login: register an account on the frontend (it becomes a **Translator**; confirm the
email in mailpit), or configure `AdminUser:Email`/`AdminUser:Password` for auth-api and the
seeder creates an **Admin** at startup.

Known local pitfall: if PostgreSQL greets you with error `28P01` (password authentication
failed), your `.env` password does not match the one the Postgres volume was initialized with —
recreate the volume or align `.env`; re-running `down -v && up` with the *same wrong* `.env`
changes nothing.

### 10.3 The production-parity stack

```bash
scripts/up-prod.sh --build
```

A **separate** compose file runs all four images (auth, tms, frontend, migrator) **plus a Caddy
reverse proxy**, under `ASPNETCORE_ENVIRONMENT=Production`: real OpenIddict keys (generated
into `.env.prod` on first run), TLS from a locally-minted CA, hosts-file entries
`app|auth|tms.lotro.test`, persistent keyring volumes, PostgreSQL over SSL. Purpose: catch
production-only breakage (HTTPS-only OpenIddict, forwarded headers, keyrings, cert trust) on a
laptop, before any cloud deploy. The proxy gives every app **one public origin** reachable
identically from the browser and from inside the network — which is exactly how the
containerized-OIDC problem dissolves in real production too.

---

## Part 11 — CI/CD and production

### 11.1 The pipelines (GitHub Actions, 13 workflows)

| Workflow | When | What it guards |
|---|---|---|
| `pr-verify` | every PR | **the merge gate**: SSR-purity check → Docker restore-graph check → migration-safety check (+ its self-test) → backlog-loop provenance-gate self-test → Release build with **zero warnings** (warnings are errors repo-wide) → unit tests → integration tests (real PostgreSQL); plus a build-and-Trivy-scan of each image |
| `ci` | push to main | the same checks post-merge |
| `cd` | after CI succeeds | build 4 images once → security-scan (Trivy, fails on fixable HIGH/CRITICAL) → sign (cosign, keyless) + provenance + SBOM → auto-deploy **staging** → wait for human approval → deploy **production** |
| `deploy` | reusable | the health-gated rollout described below |
| `n1-compat` | PRs touching migrations | previous release's integration tests against the new schema (Part 6.3) |
| `mutation-test` | PRs touching Domain/SharedKernel | Stryker, break at 67% |
| `e2e` | manual + PRs touching package/Docker dependency manifests (so Dependabot bumps exercise it) | full-stack and browser E2E suites |
| `smoke` | after deploys | health + a real OIDC token round-trip + file distribution |
| `health-ping` | daily cron | probes the three public origins once a day (deep `/health`) — the only availability signal, and the one check that proves the (scale-to-zero) Neon database is reachable |
| `cd-janitor` | nightly cron | cancels CD runs left `waiting` at the `production` approval gate for more than 24 h, so a stale-SHA candidate can never be approved by accident (#527) — via the force-cancel API endpoint, because a plain cancel is a silent no-op on approval-gated runs (#592) |
| `codeql` | PRs + weekly schedule (no push — squash-merged code was already scanned on the PR, #526) | static security analysis |
| `gitleaks` | PRs/pushes | secret scanning |
| `actionlint` | every PR | lints `.github/workflows/` so workflow-only PRs cannot merge unparsed |

### 11.2 How a release reaches production

1. Merge to `main` → CI green → CD builds **one immutable image per commit** (`sha-<short>`).
2. Images are vulnerability-scanned, signed, and published with provenance — the deploy later
   **refuses unsigned or unattested images** (fail-closed supply-chain check).
3. **Staging deploys automatically.** Staging is a full, structurally identical environment
   (own database, own secrets, own domain `staging.lotro-translator.pl`).
4. A human clicks **Approve** in GitHub → the *same image* is promoted to production.
5. Each deploy runs the same choreography: pin tags to digests → run the **migrator job**
   (with a Neon snapshot branch taken just before — a durable restore point) → start the new
   revision at **0% traffic** → poll readiness → run smoke tests against the candidate →
   flip traffic to 100% and **deactivate every superseded revision** (exactly one active
   revision per app — a leaked 0%-traffic revision once kept probing the database for days;
   ADR-0029) → smoke again.
   **Any failure → automatic rollback** to the previous revision (re-activated first, so the
   rollback pays a cold start); a bad release never serves users. Rollback moves *code only* —
   never the schema (that is why N-1 compatibility exists).

### 11.3 The production platform

- **A Hetzner VPS running `docker compose` behind Caddy** (ADR-0034). Two CX23 boxes: one carries
  both prods, one carries both stagings — **one box = one environment**, and the box's single
  `chmod 600` `/opt/lotro/.env` is what makes it prod or staging. Caddy terminates TLS with
  automatic Let's Encrypt certs, one vhost per app; only Caddy publishes ports, so the apps are
  reachable *only* through the proxy. There is **no IaC**: the "infrastructure" is a box, a compose
  file and an env file.
- **App code is cloud-agnostic** (ADR-0008): plain containers, HTTP on :8080, non-root, JSON logs
  to stdout, all configuration via environment variables, health endpoints. That neutrality is not
  theoretical — it is what made the Azure→Hetzner move a matter of re-pointing images and env vars,
  with **zero application-code changes**, in a day.
- **Secrets** live in the box's `/opt/lotro/.env` (`chmod 600`, owner `deploy`), never in git.
  Previously they lived in Azure Key Vault (ADR-0013) — and the migration produced the lesson:
  with the subscription disabled, Key Vault still served secret *names* but refused every *value*,
  so **nothing was recoverable**. Every secret was re-minted. The keeps-you-honest property is that
  they *could* be: no secret in this system is irreplaceable, only inconvenient.
- **Databases on Neon** (serverless Postgres) — unchanged by the move, which is why nothing was
  lost: scale-to-zero when idle, 6-hour point-in-time restore on the free plan, plus the
  pre-migration snapshot branches every deploy cuts. Accepted risk (zero users): no off-platform
  backups yet; the revisit trigger is the first real translators.
- **Telemetry: honestly, a gap right now.** The apps still emit vendor-neutral OpenTelemetry and
  `OTEL_EXPORTER_OTLP_ENDPOINT` is still wired, but the sink died with the subscription —
  Application Insights, the Log Analytics workspace and every Azure Monitor alert rule are gone
  (ADR-0016 and the alerting ADRs are obsolete-by-platform). Today the only availability signal is
  the **daily GitHub Actions health ping** plus the post-deploy smoke. A real telemetry sink is a
  deliberate later decision — accepted on purpose for a pre-launch, zero-user system.
- **FinOps — the decision that outranked all the tuning.** ADRs 0020/0025/0027/0029 squeezed a
  free student subscription with scale-to-zero, a scheduled warm window and a revision sweep; the
  credits ran out anyway. A flat-price €4-ish box removes the entire problem class structurally:
  no replicas to schedule, no revisions to sweep, no cold starts to hide. The one FinOps ruling
  that **survives** is ADR-0025 (readiness probes stay **DB-free**) — because Neon still scales to
  zero, and an always-on readiness ping would keep the database awake 98% of the time. The
  platform polling your health endpoint every few seconds is a hidden client of everything that
  endpoint touches.

---

## Part 12 — Defending the design (interview Q&A)

Short, honest answers with trade-offs. Each maps to an ADR or spec you can cite.

**1. Why two bounded contexts sharing a *file*, not one system or a shared database?**
Because the two halves have incompatible physics: the patcher must run 32-bit Windows-native
(x86 `datexport.dll`); the TMS is Linux containers + PostgreSQL. Any shared code layer would
poison one side (Windows deps in Docker, or web deps on a gaming PC). A version-controlled text
format is the loosest possible coupling, testable with golden fixtures on both sides, and it
matches the real workflow (files already travel between machines). Trade-off: two parsers can
drift — guarded by parity tests, and the format changes only via ADR. *(ADR-0002)*

**2. Why no MediatR?**
The mediator added indirection nobody consumed: no pipeline behaviors in real use, wiring
invisible until runtime, "find usages" broken, plus a vulnerable transitive dependency. We kept
what matters from CQRS — one use case = one record + one handler — and inject the **closed
handler interface** directly. Compile-time wiring, greppable call sites, one explicit DI line
per use case. Trade-off: no single choke-point for cross-cutting concerns — accepted, because
validation and logging live in handlers by convention. *(ADR-0001; the KittySaver reference is
mediator-based, so every lifted slice is consciously de-mediatorized.)*

**3. Why Result instead of exceptions?**
Business failures are expected outcomes, not emergencies: "cannot approve without text" is
domain information. Results make failure part of the method signature — callers must handle it;
the compiler is the reviewer. Exceptions remain for programmer errors only (guards). The API
maps `Error.Type` to HTTP codes in one place, so the error contract is uniform. Trade-off: some
ceremony (`IsFailure` checks) — worth it for exhaustiveness.

**4. Why CQRS (separate read models) from day one, in a small system?**
Because the two sides want opposite things: writes want small, rule-enforcing aggregates; lists
want flat, join-shaped, filterable rows. Same tables, two mappings — no sync, no duplication,
just two views of one truth. Query handlers physically *cannot* mutate (no-tracking context,
no behavior methods). Cost: a read model + config per aggregate — minutes of work each.
*(ADR-0002 amendment)*

**5. Why vertical slices instead of layered architecture?**
Change locality. A feature request touches one file (+ its DTOs and tests), not five layers.
Deleting a feature is deleting a file. Slices share the domain and infrastructure underneath,
so there is no duplication of rules — just no *horizontal* layer of services that every change
must tunnel through.

**6. Why hand-written strongly-typed IDs?**
Type safety (a `TranslationId` is not a `GameVersionId` — compiler-enforced), GUID v7 for
index-friendly ordering, and two deliberate factories: validated `Create` for untrusted input,
unvalidated `FromValue` for trusted rehydration. The library we used first was abandoned and
its generated public constructor *skipped validation* — 30 lines per ID is cheap ownership.
*(ADR-0010)*

**7. Why is the distributed file a "projection", not an aggregate?**
It guards no rule and is fully re-computable from translations — derived data. Modeling it as
an aggregate (as first happened) was a category error: it polluted the domain and misused the
repository abstraction. It became an immutable row behind a tiny store port. General lesson:
**not everything is DDD** — aggregates are for protected state. *(ADR-0007)*

**8. Why rebuild that file in the background with a debounce?**
The inline version paid O(catalog) serialize+hash on *every* approve, under a process lock —
k rapid approvals = k serialized full rebuilds, and a client disconnect could leave the file
stale. Now writes just signal; a worker coalesces a burst into one rebuild ~2 s later and
retries on failure. Accepted: eventual consistency (seconds) and a single-replica assumption —
both documented with revisit triggers. *(ADR-0021)*

**9. Why ETag + a pre-built file for distribution?**
Patch-day traffic is many clients asking "anything new?". The answer must cost nothing: the
file is built once per change, served as one row, and `If-None-Match` turns the common case
into a 304 with no body. The CLI keeps a cached copy and works offline — a launch never blocks
on the server. *(spec 0001)*

**10. Why self-hosted OpenIddict rather than Auth0/Entra?**
Control (custom Polish UX, custom rules like ADR-0022), zero per-user cost, no external
dependency for a hobby-scale system — and the module was already proven in the reference
project. Standards-compliant OIDC means swapping to a SaaS later is feasible. Owned risk: key
management and security updates are ours; mitigated by fail-fast key validation, rotation
support, and lifted, tested code.

**11. Why lazy provisioning instead of creating the TMS profile at registration?**
Registration-time provisioning is a distributed transaction across two systems — if the TMS is
down, registration breaks, and back-fill is manual. Lazy get-or-create on first authenticated
request is idempotent (unique index + race handling), self-healing (profiles converge whenever
claims change), and needs no cross-system saga. First tried "on first write"; browsing-only
users then had no profile — amended to "first request". *(ADR-0004)*

**12. Why email login with a separate display username?**
The implementation had drifted: login by username, charset by framework accident, and duplicate
case-variant emails possible — which would permanently lock users out once login moved to
email. The ADR made the product rule physical: email = unique login (DB-level unique index),
username = unique ASCII display handle, enforced from one shared constant in three layers.
*(ADR-0022)*

**13. Tell me about a production incident.**
The first real 79 MB import OOM-killed the API container (0.25 vCPU / 0.5 GB): the handler
materialized the file and the catalog several times over. Diagnosis surfaced a second, sneaky
copy: EF Core's retrying execution strategy *buffers entire result sets*, so even the "streaming"
catalog read was buffered — the fix uses a raw data reader for that one query. The redesign
(spec 0006) made the whole import two-pass and chunked: nothing may hold the file or catalog in
memory; added rows stream into binary COPY; mutations apply in 5k chunks with the change tracker
cleared. The temporary "give it 4 GB" bridge was reverted the same week. Numbers: ~3 minutes →
seconds, flat memory. *(ADR-0011, specs 0004/0006)*

**14. Why forward-only, N-1 compatible migrations?**
Because the deploy gate migrates the schema *before* traffic moves, and rollback re-activates
*old code on the new schema*. So every migration must keep the previous release alive, and
`Down()` is a lie in shared environments (it cannot un-write data). Destructive changes ship as
expand → backfill → contract. Enforced twice: a CI text gate with an explicit in-file
acknowledgement escape hatch, and an executable proof — the previous release's integration
suite runs against the HEAD schema in CI. *(ADR-0023, ADR-0024)*

**15. Why `xmin` for concurrency instead of a version column?**
PostgreSQL already maintains a row-version (`xmin`) on every row — zero schema cost, no code in
the domain (a shadow property in the EF mapping), and it closes a real race: approve vs import
overwriting an invalidation. Stale writes surface as 409, and the client re-reads. Optimistic
concurrency fits because conflicts are rare; locking would punish the common case.

**16. Why HATEOAS?**
One brain for permissions. The server computes which actions exist for *this caller* on *this
resource in this state* and sends them as links; the UI renders only what it received. No
duplicated role/status logic to drift, and the pattern was already the reference project's
convention. Opt-in via a vendor media type keeps plain JSON clean for ordinary clients.
*(spec 0002)*

**17. How do you *know* translations survive game updates?**
Empirically. Eight live tests across real updates (including major 47.2→48.0, 48.0→48.7 and
the 48.8 cycle) verified through four independent channels each time: in-game text, export
presence, diff content (0 Polish matches in changed hunks), and launch logs. Root cause
understood: the launcher patches the DAT in chunks, touching only changed offsets. Also proven:
the DAT's internal version number is *useless* as a content signal (unchanged across years of
updates) — the forum release-notes title is the only reliable version source. These findings killed
whole planned features (file protection, vnum triggers, re-patch-after-update) — recorded in
`docs/knowledge-base/` so nobody re-invents them. *(Lesson: measure before building.)*

**18. What are the known limits, and when do they bite?**
Consciously accepted, each with a written trigger: single API replica (in-process rebuild
queue) — bites at horizontal scale; no off-platform DB backups beyond Neon PITR + snapshot
branches — revisit at first real users; outage detection is a **once-a-day** health ping and
off-hours requests pay a ~40 s cold start (probe/replica economy, ADR-0027) — revisit at first
real users; artifact content stored in a DB row — fine at ~10 MB, revisit if it grows;
version registration and the export upload stay manual (ADR-0030). Knowing *where the cliff is*
is part of the design.

---

## Part 13 — Glossary

| Term | Meaning |
|---|---|
| **ADR** | Architecture Decision Record — a short document: context, decision, consequences. The project has 31 (Appendix A). |
| **Aggregate** | A small cluster of domain data changed only through methods on its root object, which enforce the business rules. |
| **Artifact** | The pre-built `||` file with all approved translations, served to patchers. |
| **Bounded context** | A self-contained model world with its own language and code. Here: the patcher and the TMS. |
| **CQRS** | Separating writes (commands, via aggregates) from reads (queries, via read models). |
| **DAT file** | LOTRO's giant binary container holding all game assets, including texts. |
| **Debounce** | Waiting a short moment to merge a burst of triggers into one action. |
| **ETag / If-None-Match** | HTTP revalidation: the server labels content with a hash; the client asks "changed since this label?"; "no" costs almost nothing (304). |
| **FragmentKey** | `(FileId, GossipId)` — the stable identity of one game text across versions. |
| **HATEOAS** | Responses carry links describing what the caller may do next; the UI follows links instead of hardcoding rules. |
| **Idempotent** | Safe to run twice — the second run changes nothing (re-import, provisioning). |
| **Invalidation** | Marking a translation NeedsReview because its English source changed. |
| **JWKS** | The auth server's published public keys; resource servers use them to verify token signatures offline. |
| **JWT** | A signed token (`header.payload.signature`) carrying claims like `sub` and `role`. |
| **Migration (N-1 compatible)** | A schema change that the *previous* app release can still run against. |
| **OIDC / OAuth2** | The standard protocols for login and delegated access; we use the authorization-code + PKCE flow. |
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

Plain-language summaries. The full documents live in `docs/adr/`.

**ADR-0001 — Slim handlers instead of a mediator.**
What: dropped the mediator library; commands/queries stay, but callers inject the exact handler
interface and call it directly. Why: the mediator's magic hid wiring, its pipeline did nothing
real for us, and it dragged in a vulnerable package. Day to day: one explicit DI line per use
case; validation returns `Result` instead of throwing.

**ADR-0002 — Two bounded contexts; TMS lifted from the reference project.**
What: patcher and TMS live in one repo but share only the `||` file format; the TMS copies
TheKittySaver's proven patterns 1:1, then removes the mediator. Why: incompatible runtimes
(Windows-native vs Linux web) and a solo maintainer's time. Amended over time: read models and
per-system primitives joined the lift; the patcher went from "frozen" to "stable — refactor
with care".

**ADR-0003 — One canonical form for game versions.**
What: `48`, `48.0`, `48.0.0` are stored and compared as one value (`48`). Why: raw strings made
them three different versions, silently breaking duplicate checks. Day to day: version input is
canonicalized before any comparison.

**ADR-0004 — The TMS owns a lean Translator profile, provisioned lazily.**
What: a small local record (auth ID + name + email) created automatically on a user's first
authenticated request; translations reference it. Why: lists must show *other* users' names,
which a viewer's token cannot resolve. Amended: provision on first *request*, not first write
(browsers-only users had no profile).

**ADR-0005 — Persist the frontend's Data Protection keys.**
What: cookie-encryption keyrings live on a mounted volume with a fixed app name; non-dev fails
fast without a path. Why: default keys die with the container — every redeploy would log
everyone out and break login mid-flight.

**ADR-0006 — In dev, the apps run on the host; Docker runs only infrastructure.**
What: `compose.yaml` boots postgres/migrator/mailpit/dashboard; the three apps run via
`dotnet run`. Why: a containerized OIDC client cannot make one `localhost` Authority work for
both the browser and the container; and host processes give hot reload and breakpoints.
Amended: initially just the frontend, later all three apps.

**ADR-0007 — Read projections are not aggregates.**
What: the pre-built translation file became a plain projection behind a small store port, not
an aggregate with a repository. Why: it guards no rule and is fully regenerable — modeling it
as domain state was a category error.

**ADR-0008 — Cloud-agnostic deployment strategy.**
What: apps ship as plain containers (HTTP on :8080, env-var config, JSON logs, non-root)
behind any TLS ingress; a separate `compose.prod.yaml` reproduces the production topology
locally. Why: the cloud provider was undecided, and the maintainer refused to run infra they
did not understand. Several sections later superseded by the concrete CD/migration ADRs.

**ADR-0009 — Browser E2E via Testcontainers + Playwright.**
What: the browser test boots the entire stack (DB, auth, TMS, frontend, mail, browser) as
containers from one C# fixture; plain `dotnet test` runs it. Why: in-container DNS gives one
Authority URL for browser and back-channel, and the repo already spoke Testcontainers — one
idiom, no compose scripts for tests.

**ADR-0010 — Hand-written strongly-typed IDs.**
What: replaced the ID-generator package with ~30-line hand-rolled record structs (validated
`Create` vs trusting `FromValue`) plus one generic JSON and EF converter. Why: the package was
stale and its generated constructor skipped validation.

**ADR-0011 — PostgreSQL COPY for the import's added rows.**
What: bulk-insert added rows through Npgsql's binary COPY behind a port, inside the import
transaction. Why: per-row EF inserts made a full import take ~3 minutes; the added-rows path
has no business logic to lose. Amended by spec 0006: COPY now consumes a stream, not a list.

**ADR-0012 — The continuous deployment pipeline.** *(Rollout target superseded by ADR-0034 — the pipeline shape stands; the last hop is now ssh + `docker compose`.)*
What: merge → one immutable image per commit → gated deploy to Azure Container Apps; Terraform
owns infrastructure shape; the pipeline owns which image runs; keyless OIDC auth to Azure.
Why: the previous setup silently never rolled out new code. Heavily amended as the pipeline
matured (scan/sign/attest, health-gated 0%→100% rollout, CI-must-pass).

**ADR-0013 — Key Vault is the single source of truth for production secrets.** *(Obsolete by platform — ADR-0034.)*
What: the 8 prod secrets live only in Azure Key Vault; apps read them at runtime via managed
identity; Terraform wires references without seeing values. Why: secrets previously sat in
three places (disk, TF state, GitHub) — three leak surfaces and three rotation chores.

**ADR-0014 — Production database on Neon.**
What: one Neon project, two PostgreSQL 18 databases, direct endpoint; the Supabase→Neon move
was literally rotating two connection-string secrets. Why: Supabase's free tier needed two
projects and a keepalive against 7-day pauses; Neon suspends and wakes automatically.

**ADR-0015 — Derive cross-boundary dependency versions at runtime.**
What: the Playwright browser-image tag is computed from the resolved NuGet package version
instead of a hard-coded string. Why: Dependabot bumped the package, the hidden string did not
follow, and the E2E suite broke on a protocol mismatch. Rule: one source of truth per version.

**ADR-0016 — Cloud telemetry via the platform's managed OpenTelemetry agent.** *(Obsolete by platform — ADR-0034; no sink today.)*
What: enable Azure Container Apps' built-in OTel agent so existing vendor-neutral telemetry
lands in Application Insights with zero app-code change. Why: the apps already emitted clean
telemetry, but nothing exported it — production was nearly blind.

**ADR-0017 — Parametrized infrastructure per environment.** *(Obsolete by platform — ADR-0034; there is no IaC left.)*
What: one Terraform root, parametrized by `env_id` and one base-domain variable; separate state
file per environment; no premature module extraction. Why: "two environments" was promised but
the Terraform was hard-coded to one.

**ADR-0018 — Staging + two-stage promotion.**
What: build once → auto-deploy staging → a human approves promotion of the *same image* to
production. Why: prod doubled as QA. Reality bite: the subscription allows one Container Apps
environment, so staging shares prod's environment while keeping its own DB, secrets, identity
and domain.

**ADR-0019 — Symptom-based alerting via external probes.** *(Obsolete by platform — ADR-0034; the Azure alerting stack is gone.)*
What: synthetic web tests hit the public URLs from three regions; alert on quorum failure;
plus Key Vault and auth-latency alerts. Why: the previous log-based alert fired a false Sev0 on
*every healthy deploy* — alert fatigue that would mask a real outage.

**ADR-0020 — FinOps right-sizing.** *(Obsolete by platform — ADR-0034; a flat-price box removes the problem class.)*
What: staging scales to zero replicas; probe cadence drops 5→15 minutes. Why: a cost review
found the two biggest line items were probes (~$47/month) and idle staging — not the product.
Accepted: slower worst-case detection, cold starts on staging.

**ADR-0021 — Debounced background artifact rebuild.**
What: writes signal a background worker; one rebuild per burst per language; the artifact row
became immutable, refreshed by a set-based update. Why: inline rebuilds serialized every
approve behind an O(N) scan and could leave the file stale on client disconnects.

**ADR-0022 — Email is the login; username is a display-only handle.**
What: login by email everywhere; username restricted to ASCII letters/digits, unique, display
only; unique email index at the database level. Why: implementation had drifted from the
product rule, and case-variant duplicate emails could permanently lock users out.

**ADR-0023 — Migration safety: forward-only, N-1 compatible.**
What: never run `Down()` in shared environments; every migration must keep the running release
alive; destructive changes go expand → backfill → contract over ≥2 deploys; a CI text gate
requires an explicit in-file acknowledgement for destructive statements. Why: rollout and
rollback both run old code against the new schema — that contract existed only implicitly.

**ADR-0024 — The N-1 proof is executable.**
What: on migration PRs, CI checks out the *previous release* and runs its integration suites
against the *new* schema in a fresh container. Why: ADR-0023 was a rule and a text scan;
nothing actually executed old-code-on-new-schema until this.

**ADR-0025 — Readiness probes must not touch the database.** *(Still binds — Neon still scales to zero.)*
What: `/health/ready` only proves the app serves HTTP; the deep `/health` (DB + SMTP) stays
for operators and smoke tests. Why: the platform's frequent readiness ping kept the
scale-to-zero database awake ~98% of the time and was about to exhaust the free plan. Accepted:
a broken connection string passes readiness — the deploy smoke gate catches it instead.

**ADR-0026 — Only maintainer-written issues may drive the autonomous loop.**
What: the backlog loop refuses any GitHub issue whose author *or any commenter* lacks write
access (`scripts/claude/issue-trust.sh`, fail-closed, enforced in front of every session). Why:
the repo is public and the loop auto-merges the result — untrusted issue text would be a
prompt-injection channel straight to `main`.

**ADR-0027 — Prod's warm replica comes from a schedule; the availability probe leaves Azure.** *(Partly obsolete — the warm window died with Azure (ADR-0034); the daily ping survives as the only availability signal.)*
What: `min_replicas = 0` everywhere; production keeps one warm replica only inside a KEDA cron
window (07:00–22:00 Europe/Warsaw); the three-region Azure web tests are deleted, replaced by a
daily GitHub Actions health ping. Why: six always-on replicas served zero users and the probes
themselves kept scale-to-zero apps awake — the credit was ~10 days from exhaustion. Accepted:
off-hours requests pay a ~40 s cold start; outage detection is daily.

**ADR-0028 — Docker restore layers are loud and gated.**
What: every Dockerfile must COPY the full transitive closure of the projects it restores;
builds run with `--no-restore` so a gap becomes a hard error, and a CI script gate
(`check-dockerfile-restore-graph`) runs on every PR. Why: `dotnet restore` silently skips
missing project files (exit 0), caching an incomplete restore layer. One exception: the
frontend image's `dotnet build` keeps restore on, or the SDK drops `blazor.web.js`.

**ADR-0029 — Exactly one active revision per app.** *(Obsolete by platform — ADR-0034; there are no revisions.)*
What: the rollout's promote step deactivates *every* superseded revision (not just the one
holding traffic) and fails loudly if it cannot; rollback re-activates the previous revision and
pays a cold start. Why: a leaked 0%-traffic revision with an old image probed the database every
30 s for 8 days — invisible, and it held the "scale-to-zero" promise open.

**ADR-0030 — The game-version export stays manual; the VM runner is deferred.**
What: producing `exported.txt` after a game update remains a manual admin task; the unattended
Windows-VM runner idea is parked (prerequisites unconfirmed, no cheap KVM host, zero users hurt
by the staleness window). The manual pipeline was instrumented instead — notably the patcher's
read paths no longer demand elevation. Why: YAGNI with a written revisit trigger.

**ADR-0031 — GDPR account deletion runs through a 14-day grace period.**
What: `DeleteAccount` no longer erases immediately — it schedules deletion, locks the account
for a 14-day window (capped at 30 by options validation, inside GDPR Art. 12(3)'s one month),
revokes sessions and tokens, and emails a one-time cancel link; a background finalizer performs
the actual anonymization after the window, and cancelling forces a password reset. Why: with
password-only confirmation, one credential-stuffing hit could irreversibly erase an account;
the industry-standard grace window gives the legitimate owner a recovery path (ported from
TKS ADR-0017).

**ADR-0034 — A single Hetzner VPS instead of Azure Container Apps.**
What: on 2026-07-12 the Azure for Students subscription was disabled — credits exhausted, renewal
refused — and both prods went dark. Prod + staging moved to Hetzner boxes running `docker compose`
behind Caddy; Neon stayed the database, GHCR stayed the registry, and CD's last hop became ssh +
`docker compose` instead of an ACA revision rollout. Why: the app code was already cloud-agnostic
(ADR-0008), so the move cost **zero application changes** — and a flat-price box structurally
deletes the whole FinOps problem class the ADRs above had been fighting (scale-to-zero, warm
windows, revision sweeps). Accepted: no blue/green (a 4 GB box cannot hold a second live set, so a
deploy costs seconds of downtime, guarded by a migration gate + automatic rollback); observability
shrank to logs + a daily health ping until a real sink is chosen; the box is now a thing to patch.
Retired with it: the Terraform root, Key Vault, and ADRs 0013/0016/0017/0020/0027/0029 — the
Terraform and the Key Vault seeders survive as a read-only tombstone in
`docs/deployment/azure-graveyard/`, so the `iac/*.tf` those ADRs argue about can still be opened.

---

## Appendix B — every spec in brief

Full documents in `docs/specs/`. A spec is written and *agreed* before implementation;
questions are extracted for the owner, never invented.

**Spec 0001 — The game-update lifecycle (Agreed; the foundation).**
Defines the domain of Part 2: game versions detected from the forum (the only reliable
signal), one upload per update, the five-outcome diff (added / source-changed / removed /
restored / unchanged), invalidation = NeedsReview with the old English frozen for side-by-side
review, "fallback to English" by excluding invalidated rows from the distributed file, one
mutable row per fragment (no per-version snapshots), the two-fold truncation guard (strict
parsing + the 20% mass-removal threshold), and the anonymous, ETag-cached, stampede-proof
distribution endpoint with the CLI's offline-tolerant sync. Explicitly cut: automated exports,
crowdsourced detection, full edit history, more languages.

**Spec 0002 — HATEOAS links on read endpoints (Implemented).**
Every read response can carry `links`: `self` plus exactly the actions this caller may perform
in the resource's current state (e.g. `approve` only for admins on Draft/NeedsReview rows).
Links are opt-in via a vendor media type; command responses carry none. Added a get-one
endpoint for game versions and a collection envelope. Point: the server is the single brain
for permissions; the UI follows links.

**Spec 0003 — Large upload support (Implemented).**
The export is ~80 MB and the default request cap was 30 MB, so imports failed at the door — on
both hops (frontend and API). Fix: a shared 256 MB ceiling (config-adjustable), an
upload-aware client timeout (5 minutes for multipart instead of 10 seconds), and a clean 413
beyond the cap. Chunked/resumable upload protocols were considered and rejected as
over-engineering (single admin, monthly operation).

**Spec 0004 — Bulk set-based import, phase 1 (Implemented).**
The full import took ~3 minutes because of per-row EF writes. Added rows now go through
PostgreSQL binary COPY inside the same transaction (~seconds), while diff mutations stayed
per-row. Every spec-0001 rule preserved byte-for-byte; "unchanged" writes nothing (frozen
timestamps prove it); plus a subtle retry-safety fix so a flaky commit cannot silently drop the
diff. Budget: full baseline < 10 s in integration tests.

**Spec 0005 — Game-versions management UI (Implemented).**
A `/game-versions` page: list for every translator; admin-only manual register (for when the
forum is down or the watcher does not exist yet) and admin-only guarded delete — only
Unprocessed, never-referenced versions can go, enforced server-side; the UI shows buttons only
when the API advertises the `register`/`delete` links.

**Spec 0006 — Streaming two-pass import, phase 2 (Implemented).**
Born from the OOM incident: the first real 79 MB / 792,500-row imports killed the container.
The import became two passes — plan (stream + validate + diff via 128-bit source hashes against
a streamed compact catalog projection) then apply (COPY for adds fed as a stream; mutations in
5,000-row chunks with the change tracker cleared) — with a hard rule that no stage may hold the
whole file or catalog in memory. Includes the EF-retry-buffering discovery (that one query uses
a raw data reader) and reverted the temporary 4 GB container bridge.

**Spec 0007 — Bulk approve from the list (Implemented).**
Checkboxes on the translations list; an admin approves up to 100 rows (one page) in one POST.
Best-effort semantics: approve everything still approvable, count the rest as skipped — one
stale row never blocks the batch; already-approved rows are no-ops (approver not re-stamped).
One transaction, one debounced rebuild (only if something was approved), Post-Redirect-Get with
a result flash. Cross-page "select all" is explicitly out (needs client-side JavaScript, which
Static SSR forbids).

**Spec 0008 — Game-content catalog (Agreed; milestone M7 — not yet implemented).**
Imports LOTRO Companion's lore XML as a **catalog lens** over the flat rows: catalog entries
(quests, deeds, … — never called "entities", to avoid the DDD collision) with role-tagged text
slots, joined to translations **by `(FileId, GossipId)` keys, never by text** (the
`key:<FileId>:<GossipId>` tokens in Companion's data — empirically verified). A translator picks
*a quest* and translates all its texts as one unit, with per-entry and per-category progress.
The lens never mutates translations and never triggers the artifact rebuild.

**Spec 0009 — Frontend "Moje konto": GDPR self-service (Implemented).**
The privacy policy promises translators self-service export and deletion "w sekcji Moje konto" —
LEGAL-01 shipped the whole backend, this spec builds the browser UX on top: view account data,
download the full JSON data export, change password, and schedule account deletion with the
exact finalization date shown; cancellation stays on the auth-side page driven by the emailed
one-time token.

**Spec 0010 — Terms of service (Implemented).**
The service ran at lotro-translator.pl with no ToS anywhere. Two gaps made it critical: LOTRO is
Standing Stone Games / Middle-earth Enterprises IP, so the platform must state its
non-commercial, non-affiliated fan-project status; and the LEGAL-01 erasure design deliberately
keeps translation contributions (anonymized) after account deletion — defensible only with an
explicit contribution license, which the ToS now grants. Registration requires accepting it
(the third consent flag).

---

*End of the handbook. If something here disagrees with the code, the code wins — and this file
should be fixed in the same pull request.*
