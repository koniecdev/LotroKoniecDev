# ADR-0010: Custom strongly-typed IDs instead of the StronglyTypedId package

**Status:** Accepted
**Date:** 2026-06-25
**Decision-makers:** Solo maintainer
**Related:** `SharedKernel/StronglyTypedIds`, `TranslationSystem.Primitives` (all `*Id` structs), `TranslationSystem.Persistence/Converters`, ADR-0002 (amendment 2026-06-12 — the `IStronglyTypedId` base lives in SharedKernel), ADR-0007 (read projections key on the same interface)

## Context

Every aggregate id in the TMS is a `partial struct` carrying a `Guid`, annotated with
`[StronglyTypedId(jsonConverter: StronglyTypedIdJsonConverter.SystemTextJson)]` and implementing
the **repo-owned** marker `IStronglyTypedId<TSelf>`
(`SharedKernel/StronglyTypedIds/Common/IStronglyTypedId.cs`). The package
`StronglyTypedId 0.2.1` (Andrew Lock) source-generates the struct body. Five ids use it:
`GameVersionId`, `TranslationId`, `TranslatorId`, `PrecomputedTranslationFileId`
(`TranslationSystem.Primitives`) and `IdentityId` (`SharedKernel`).

Code facts that constrain the choice:

- **The package is the engine, not the foundation.** The marker interface is ours; the generic
  scaffolding already keys on *it*, not on the package: `Ensure.NotEmpty<T>() where T : IStronglyTypedId<T>`
  (`SharedKernel/Guards/Ensure.cs:39`), `GenericRepository<TAggregateRoot, TAggregateRootId> where
  TAggregateRootId : struct, IStronglyTypedId<TAggregateRootId>`
  (`TranslationSystem.Persistence/GenericRepository.cs:10`), `IReadOnlyEntity<out T> where
  T : struct, IStronglyTypedId<T>` (`ReadModels/Core/BuildingBlocks/ReadOnlyEntity.cs:5`). Swapping
  the engine leaves all of this untouched.
- **The version is effectively abandoned.** `Directory.Packages.props:9` pins `0.2.1` while the
  package's 1.x line has shipped for years. Depending on a stale, unmaintained generator for a
  type-system primitive on .NET 10 is the risk this ADR removes.
- **The reference leaks transitively.** Both `SharedKernel.csproj:8` and
  `TranslationSystem.Primitives.csproj:9` declare `<PackageReference Include="StronglyTypedId" />`
  **without `PrivateAssets="all"`**, so a build-time-only generator flows as a transitive dependency
  to everything referencing those projects.
- **The generated surface is partly dead and partly a foot-gun.** The emitted struct
  (verified in `obj/.../GameVersionId.*.generated.cs`) exposes a **public** `ctor(Guid)` with no
  validation, a `New()` factory, `static readonly Empty`, `IEquatable`/`IComparable`, `==` (routed
  through `CompareTo`), `ToString()`→bare guid, a nested `TypeConverter`, and an STJ
  `JsonConverter` that serializes as a string. Of these: `New()` is **dead** (no call site; the
  code uses the hand-written `Create()` instead), the nested `TypeConverter` is **dead** (routes
  bind `{id:guid}` and wrap manually — `GetGameVersion.cs:65`, `GetTranslation.cs:89`,
  `ApproveTranslation.cs:114`, `ImportExportedTexts.cs:225`), and the public `ctor(Guid)`
  **bypasses** the `Ensure.NotEmpty` that `Create(Guid)` performs — validation is silently
  skippable, and the endpoints do skip it.
- **EF conversion is already hand-rolled, five times over.**
  `TranslationSystem.Persistence/Converters/StronglyTypedIdsConverters.cs` declares one
  `ValueConverter<TId, Guid>` subclass per id (lines 52–70) — the package contributes nothing here.
- **JSON travels with the type, not via global registration.** `ConfigureHttpJsonOptions` adds only
  `JsonStringEnumConverter` (`TranslationSystem.API/ApiDependencyInjection.cs:54`); ids serialize
  purely through the generated per-type `[JsonConverter]` attribute, so the same id round-trips
  correctly in the API, the Frontend HttpClients, and the E2E clients without any host knowing about it.

## Decision

### 1. Drop the package; own the struct

Remove `StronglyTypedId` from `Directory.Packages.props` and both `.csproj` files. Each id becomes
a hand-written `public readonly record struct` implementing `IStronglyTypedId<TSelf>`. The compiler
supplies `Equals`/`GetHashCode`/`==`/`!=`/`IEquatable<TSelf>` (the per-type equality that value
types cannot inherit), so the struct body stays ~8 lines.

### 2. Validation cannot be bypassed — private ctor, two static factories

The constructor is **private**. The interface gains a second, **non-validating** factory so
infrastructure can rehydrate from a trusted store without re-running domain guards:

```csharp
public interface IStronglyTypedId<out TSelf> where TSelf : IStronglyTypedId<TSelf>
{
    Guid Value { get; }
    static abstract TSelf Create();          // domain: new id (Guid v7)
    static abstract TSelf Create(Guid id);   // untrusted input: Ensure.NotEmpty
    static abstract TSelf FromValue(Guid id);// trusted rehydration (EF/JSON): no validation
}
```

`Create(Guid)` is the only path from untrusted input and always validates; `FromValue` is the only
path used by the EF and JSON converters. There is no public `new XId(guid)` to skip the guard.
(`default(TId)` still exists — unavoidable for any value type — and is handled exactly as today via
the retained `Empty`.)

### 3. One generic JSON converter, pointed to per type

A single `StronglyTypedIdJsonConverter<TId> : JsonConverter<TId> where TId : struct,
IStronglyTypedId<TId>` (in SharedKernel) reads a string→`TId.FromValue(Guid.Parse(...))` and writes
`value.Value`. Each id keeps the attribute form, now closed over itself:
`[JsonConverter(typeof(StronglyTypedIdJsonConverter<GameVersionId>))]`. The conversion keeps
travelling with the type — API, Frontend, and E2E need **zero** new global registration.

### 4. One generic EF converter, replacing five classes

A single `StronglyTypedIdValueConverter<TId> : ValueConverter<TId, Guid>` (`id => id.Value`,
`v => TId.FromValue(v)`) replaces the five hand-written subclasses. The explicit per-id
registration list in `RegisterAllStronglyTypedIdConverters` stays (it doubles as an id inventory);
only the converter *bodies* collapse to one generic type. The `UtcDateTimeOffsetConverter` in that
file is unrelated and untouched.

### 5. Keep the live surface, drop the dead surface

Retain `Value`, `Empty`, value equality, and `ToString()`→bare guid (the JSON/log/link contract).
Drop the generated `New()` (dead) and the nested `TypeConverter` (dead). `IComparable<TSelf>` is
**not** emitted by `record struct`; add it per id **only if** the zero-warning build proves a real
consumer — not speculatively.

### 6. Migrate the call sites

Replace every `new XId(guid)`. The EF sites vanish into the generic converter. **Route endpoints
wrap the `{id:guid}` route value with `XId.FromValue(id)` — not `Create`** — so an empty id stays a
`Result.Failure(Validation)` from the handler's existing `== Empty` guard rather than an
`ArgumentException`, honoring the "errors are values" house rule; the handler guard (and its tests)
therefore stays. Test fixtures and domain/FK construction use the validating `Create(guid)`.
`CurrentUserAccessor` wraps the JWT subject with `FromValue` (unchanged non-validating behavior).

## Consequences

### Positive

- One fewer third-party dependency for a core type-system primitive — and a *stale, unmaintained*
  one at that; no transitive leak from a build-time generator.
- Validation is structurally unbypassable: the only constructor is private, the only untrusted entry
  is the guarding `Create`.
- Less code than today, not more: five EF converter classes collapse to one generic; the dead
  `New()` and `TypeConverter` are gone; one idiom for creating ids instead of two (`Create` vs `New`).
- Fully ours and debuggable on .NET 10 — no generated `obj` artifacts to reason about, plain source.
- No behavioural change for serialization: ids still round-trip as JSON strings via the attribute, so
  API/Frontend/E2E are unaffected.

### Negative / Accepted Trade-offs

- Per-id equality is now in source (compiler-generated by `record struct`) rather than hidden in a
  generator — trivially more lines per id, but visible and inert.
- `static abstract` members put a small generic-math-style ceremony on the interface; the cost is one
  extra factory method (`FromValue`) every id must declare.
- A one-time mechanical sweep of ~25–30 `new XId(guid)` call sites (EF, endpoints, test fixtures).
  Guarded by the existing build (`TreatWarningsAsErrors`) and the integration test suite.
- If a future id must key on something other than `Guid` (e.g. `long`), the interface's `Guid Value`
  shape would need revisiting — acceptable: every current id is a `Guid` (v7), and YAGNI applies.

## Alternatives Considered

### A. Keep `StronglyTypedId 0.2.1`

Zero work, already wired. **Rejected.** It is the exact dependency the maintainer distrusts —
abandoned version, transitive leak, dead generated surface, and a validation-skipping public ctor.

### B. Write our own incremental source generator

Reproduce `[StronglyTypedId]` as a first-party Roslyn generator. **Rejected.** A separate
`netstandard2.0` analyzer project plus its own test suite and generator-debugging tax, to emit ~8
lines for **five** ids — a factory built for five screws. Violates the repo's right-size/YAGNI rule.
Revisit only if the id count grows by an order of magnitude.

### C. Fully hand-written structs, no shared infrastructure

Each id spells out equality, JSON, and EF conversion inline. **Rejected.** ~60 lines of duplicated
boilerplate × 5, with real drift risk between types; the generic JSON/EF converters exist precisely
to avoid this.

### D. Keep the public `ctor(Guid)` (the "1:1" variant)

Public ctor as "rehydration", `Create` validates — identical to today's behaviour, zero call-site
churn, but per-type converters must stay (no static factory to drive a generic) and validation stays
skippable. **Rejected.** The maintainer chose the hard-encapsulation variant; unbypassable validation
plus the converter collapse is worth the mechanical sweep.

### E. Global `JsonConverterFactory` instead of the per-type attribute

Register one factory in every host's `JsonSerializerOptions`. **Rejected.** It moves a correctness
guarantee from "travels with the type" to "every host must remember to register it" — API, Frontend
RP, *and* the E2E clients — an easy omission. The attribute form keeps the conversion intrinsic to
the id.

## Implementation Notes

- **Changed (SharedKernel):** `StronglyTypedIds/Common/IStronglyTypedId.cs` (add `FromValue`);
  `StronglyTypedIds/IdentityId.cs` (rewrite as `record struct`, private ctor, attribute);
  new `StronglyTypedIds/Json/StronglyTypedIdJsonConverter.cs`; `SharedKernel.csproj` (drop ref).
- **Changed (Primitives):** `GameVersionId`, `TranslationId`, `TranslatorId`,
  `Projections/PrecomputedTranslationFileId` (rewrite to the new shape);
  `TranslationSystem.Primitives.csproj` (drop ref).
- **Changed (Persistence):** `Converters/StronglyTypedIdsConverters.cs` — replace the five
  `*IdConverter` subclasses with one generic `StronglyTypedIdValueConverter<TId>`; keep the explicit
  registration list and `UtcDateTimeOffsetConverter`.
- **Changed (call sites):** ~25–30 `new XId(guid)` → `XId.Create(guid)` across
  `TranslationSystem.API/Features/*` endpoints, `Auth/CurrentUserAccessor.cs`, and test fixtures
  (Frontend unit, API unit, integration, E2E).
- **Removed:** `StronglyTypedId` from `Directory.Packages.props` and both `.csproj` files; the dead
  `New()` and nested `TypeConverter`.
- **Verify:** zero-warning build (`dotnet build LotroKoniecDev.slnx`) and the full test suite green;
  add `IComparable<TSelf>` per id only if the build surfaces a consumer.

## References

- ADR-0001 — slim SRP handlers; same "own the small primitive, drop the third-party indirection" instinct
- ADR-0002 (amendment 2026-06-12) — the `IStronglyTypedId` base belongs in SharedKernel
- ADR-0007 — read projections key on `IStronglyTypedId<T>` (the constraint this ADR preserves)
- StronglyTypedId package — https://github.com/andrewlock/StronglyTypedId (1.x supersedes the pinned 0.2.1)
- .NET `static abstract` interface members / generic math — the mechanism behind the `Create`/`FromValue` factories
