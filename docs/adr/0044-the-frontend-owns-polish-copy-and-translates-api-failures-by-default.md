# ADR-0044: The Frontend owns the Polish copy for API failures, and translates by default

**Status:** Accepted
**Date:** 2026-08-06
**Decision-makers:** Solo maintainer
**Related:** #548 (the defect), #268 / #272 (the QA reports that found it), ADR-0041 (no gateway — the API is a shared contract surface), ADR-0040 (rel names are a frozen public contract), `ApiProblemCopy`, `HttpClientApiExtensions`

## Context

The Frontend is Polish-only. Every API failure it renders is English.

The pages already do the right *shape* — they carry the API's `ProblemDetails` on an `ApiResult`
and render it instead of throwing — but what they render is whatever the API wrote:

```razor
<p>@(_actionProblem.Detail ?? _actionProblem.Title ?? "Operacja nie powiodła się.")</p>
```

`Detail` is the domain error message (`"The lotronotationversion value '48.0' is already taken."`),
`Title` is the error-type name (`"Validation Error"`, `"Internal Server Error"`). The Polish string
is the **third** branch — reached only when the API says nothing at all, which is the rare case. So
a translator saving an empty translation gets a red box reading `Validation Error`, and an admin
registering a malformed game version gets the FluentValidation default `'Version' must not be empty.`

Fourteen render sites across eight pages had the same shape. Two variants existed and both are
wrong in the same way: action failures render `Detail ?? Title`, load failures render `Title` alone
— which is worse, because `Title` is *always* one of the six English type names.

Widening the fallbacks does not fix it. The fallback is not the problem; the branch before it is.

## Decision

### 1. The Polish copy lives in the Frontend, keyed by `errorCode` — the API stays English

The API already emits a machine-readable key on every failure it authors. Both
`ErrorExtensions.ToProblemDetails()` implementations stamp `Extensions["errorCode"] = error.Code`,
and all five exception handlers per API stamp a fixed code (`Validation.FluentValidation`,
`Http.PayloadTooLarge`, `Db.ConcurrencyConflict`, `Http.InvalidArgument`,
`Internal.UnhandledException`). Nothing had to change server-side to make this work; the key was
already on the wire, unused.

`ApiProblemCopy` maps that code to Polish. The alternative — localizing in the API and keeping the
Frontend a dumb renderer — was rejected:

- There is no gateway (ADR-0041). The TMS API is reached directly by the **CLI** (`export`/`patch`/
  `launch` auto-download, M2-20) and will be reached by the Avalonia app (M4). Its `ProblemDetails`
  is a shared contract, not one client's UI copy.
- The API has no notion of a caller language. Adding one means content negotiation, a resource
  pipeline and a per-locale message catalog server-side — infrastructure for a product that is
  Polish-only and has one UI.
- The existing house pattern already puts Polish in the Frontend: `HttpClientApiExtensions` writes
  its transport-failure copy inline, and every page's fallback string is Polish.

So `errorCode` becomes what a rel name is for links (ADR-0040): **a frozen contract token**.
Additions are cheap; renaming a code silently degrades that failure to the generic message. The map
covers every code both APIs can produce today.

### 2. Translation is the default; only an explicitly marked Frontend problem passes through

The Frontend authors `ProblemDetails` of its own — Polly transport failures
(`"Usługa chwilowo niedostępna"`), missing-rel 403s, inline form guards (`"Podaj wersję gry."`).
Those are already Polish and must pass through untouched. Something has to tell them apart from the
API's.

**"No `errorCode` ⇒ the Frontend wrote it" is the obvious discriminator and it is wrong.** Both APIs
run `AddProblemDetails()` + `UseStatusCodePages()`, and for every status that middleware synthesizes
— 401, 403, 404, 405, 415, 429 — the body is a bare English reason phrase with **no** `errorCode`.
The repo's own committed contract says so:

```json
{ "type": "…rfc9110#section-15.5.2", "title": "Unauthorized", "status": 401, "traceId": "…" }
```

(`ProblemDetailsSnapshotTests.ProtectedEndpoint_WithoutToken_…verified.json`, and the `"Forbidden"`
twin next to it.) Those are the *most common* failures in the app — an expired token on any page, a
non-translator opening `/game-versions` — so keying off the code's absence would have painted the
literal word `Unauthorized` into the Polish page: exactly the defect this ADR exists to remove, on
the highest-traffic path.

So the marker runs the other way, and the safe case is the default:

- **Marked `frontendAuthored`** ⇒ ours ⇒ already Polish ⇒ render `Title` + `Detail`.
- **Everything else** ⇒ translate: mapped code → status copy → call-site fallback.

Only `ApiProblemCopy.FrontendAuthored(...)` stamps the marker, and `ParseProblemDetails` strips it
from anything read off the wire, so a response body carrying that member cannot smuggle English
past the lookup.

The asymmetry is the point. Forgetting the marker on a new Frontend-authored problem shows a
slightly generic Polish sentence; forgetting to anticipate an API surface shows English. One is a
blemish, the other is the bug. The default has to fall on the blemish side.

### 3. An unmapped code degrades to Polish by HTTP status — never to English

Precedence for an API-authored problem: mapped code → status-code copy (400/401/403/404/409/413/
422/429/5xx) → a generic Polish sentence. English can never reach the screen, including when the
API ships an error the Frontend has not seen yet.

The gap is not silent: `ApiProblemAlert` logs a warning naming the unmapped code and the endpoint's
status the moment it renders one. The component is the right place for that log — it fires exactly
when a user is actually looking at a degraded message, which is the condition worth reporting, and
unlike the parsing seam it is DI-constructed and has an `ILogger`.

The status map deliberately outranks the call site's own fallback string. `"Nie udało się wczytać
statystyk."` says only that something failed; `"Nie masz uprawnień do wykonania tej operacji."` says
what to do about it. The call-site fallback stays for the one case nothing else covers — no problem
object at all.

### 4. The original English survives as collapsible technical detail

Some API messages carry numbers a static Polish string cannot reproduce: `Import.ParseFailed` names
the first bad line, `Import.MassRemovalBlocked` names how many rows of how many would be removed.
Those are the numbers an admin needs to decide what to do, and they are exactly what a code→copy
map throws away.

So `ApiProblemAlert` renders a Polish headline plus a collapsed `<details>` block carrying the code
and the API's own `Detail`. The page is Polish; the diagnostics are one click away and can be pasted
into a bug report. `<details>` is plain HTML — no interactivity, no SSR-purity cost.

The alternative of adding structured extensions to the API's import errors (`lineNumber`,
`removedCount`, …) so the Polish string could interpolate them was rejected as scope: it changes the
API for one page's copy, and the nested parser messages inside `Import.ParseFailed` would stay
English regardless.

### 5. One component, both existing shells

`ApiProblemAlert` renders the *body* — headline and `<details>` — not the box. The two established
shells stay exactly as they are: the rich `error-message` box with its icon for load failures, and
`status-message status-error` for action failures. A `HeadlineClass` parameter carries the `t` class
the rich box's CSS expects.

That keeps the visual diff at zero while making the copy lookup impossible to bypass: a page cannot
render a `ProblemDetails` without going through the component.

The two file-download routes (`polish.txt`, the GDPR account export) are the one surface with no
component to render through — they answer with `Results.Problem(...)`, and the browser shows that
body verbatim. They call `ApiProblemCopy.Localize` instead, which runs the same lookup and returns a
Polish-titled problem, parking the API's wording under a `technicalDetail` extension. Same rule, same
map, different transport.

## Consequences

### Good

- No English reaches a Polish page, for any error either API can produce — including unmapped and
  future ones.
- `Title` stops being rendered anywhere. The six English type names (`"Validation Error"`,
  `"Not Found"`, …) were never user-facing copy and are now confined to logs.
- The copy is in one file. Rewording an error is a one-line change reviewed as copy, not spread
  across eight pages.
- The API keeps serving the CLI, the M4 app and any future client with the same English contract.
- Load failures improve the most: they rendered `Title` alone, so they showed the *worst* English.

### Neutral

- `errorCode` joins rel names as a contract token: renaming one degrades the message to the status
  fallback rather than breaking anything. The degradation is logged.
- The map is maintained by hand. A new API error without copy is a visible generic message plus a
  logged warning — not a silent regression, and not a build break either.
- A new Frontend-authored problem must go through `ApiProblemCopy.FrontendAuthored`. Forgetting it
  costs specificity (the status sentence instead of the page's own wording), never correctness —
  and no raw `new ProblemDetails` is left in the Frontend for the next author to copy.

### The limit of this fix

`ValidationProblemDetails.errors` — FluentValidation's per-field English messages — still travels in
the body. Nothing renders it, and the handler's `Validation.FluentValidation` code is mapped, so it
is dead weight on the wire rather than a user-visible defect. Localizing per-field messages would
need a field-level code vocabulary the API does not have; if forms ever need inline per-field
errors, that is a separate decision.
