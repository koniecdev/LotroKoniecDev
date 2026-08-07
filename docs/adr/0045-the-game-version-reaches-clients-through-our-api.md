# ADR-0045: The game version reaches clients through our API, never a client-side forum scrape

**Status:** Accepted
**Date:** 2026-08-06
**Decision-makers:** Solo maintainer
**Related:** Patcher (`Features/UpdateChecking`, `Features/PreflightChecking`, `Features/TranslationFileSyncing`), TMS distribution endpoint, `docs/specs/0012-update-resilience.md` (Tier-0 handshake — amended by this ADR), `docs/specs/0001-game-update-lifecycle-and-translation-invalidation.md`, ADR-0030 (partially superseded), ADR-0041 (rel resolution), ADR-0002 (bounded contexts), tickets #85 (UR-24), #562 (UR-22), #565 (UR-10), #611 (CLI discovery), #624 (UR-23), #626 (UR-25), #627 (UR-26)

## Context

Spec 0012 designs a Tier-0 launch sentinel: on every `launch` the patcher samples known-translated
fragments in the DAT and, on detecting a collateral revert, force-patches with a freshly synced
artifact. Its anti-masking guard is the **handshake** — spec 0012 AC item 2 reads *"Sentinel never
patches with an artifact older than the live forum version"* — which as written puts a live
lotro.com read on every player's machine at every launch.

Code facts that constrain the choice:

- **The client's forum scrape exists, but nowhere near `launch`.**
  `GameUpdateChecker` (`Features/UpdateChecking/GameUpdateChecker.cs`) owns the regex
  `Update\s+(\d+(?:\.\d+)*)\s+Release\s+Notes` (`:83`) and `ForumPageFetcher` hardcodes the forum
  URL (`Infrastructure/Network/ForumPageFetcher.cs:20`). Its only consumer is
  `PreflightCheckQueryHandler` (`:44`), and that query is issued by `PatchCommand` alone
  (`Cli/Commands/PatchCommand.cs:67`). The launch path never calls it:
  `SimplifiedGameLaunchingStrategy` reads a **stored** `StoredVersionInfo.ForumVersion` from the
  local version file (`:69`), so at launch that value is only as fresh as the last `patch`.
  Implementing spec 0012's handshake literally means adding a *new* third-party network call to
  the hot path of every installed client.
- **Players are anonymous; the version catalog is not.**
  `GET /api/v1/translation-files/{lang}` is `.AllowAnonymous()` (`GetTranslationFile.cs:135`) —
  that is how the CLI syncs without credentials. `GET /api/v1/game-versions` requires the
  translator role (`ListGameVersions.cs:109`). A player therefore cannot query the TMS for the
  current game version today; whatever the client needs has to ride on the distribution response.
- **The artifact carries no version at all.** `PrecomputedTranslationFile` holds `Language`,
  `Content`, `ContentHash`, `GeneratedAt` — nothing else; grep for `GameVersion` across
  `TranslationSystem.Projections` and `API/Features/TranslationFiles/` returns zero hits.
  #562 (UR-22) states the artifact "already knows the GameVersion it was generated for" — it does
  not. That version must be introduced (schema + rebuild), not merely exposed.
- **Version ordering is domain knowledge the TMS already owns.** `LotroNotationVersion` is the
  constrained-string VO for LOTRO's notation, and the import slice already reasons about version
  precedence — `ImportExportedTexts` supersedes older unprocessed versions by `DetectedAt`
  (`:321`–`:333`). A shipped CLI cannot be redeployed when that notation surprises us.
- **No local signal substitutes for the version.** DAT vnum is empirically useless as a content
  version — 112/3 unchanged across 45.x→49.1, six cycles including two majors
  (`docs/knowledge-base/`).
- **The TMS's own knowledge of "current version" is manual today.** ADR-0030 kept game-version
  registration a manual ceremony and left the M2-18 forum watcher (#85) in Post-MVP/Backlog,
  amending its scope only to add an e-mail alert (§2). `RegisterGameVersion` is documented as
  "the degenerate fallback the admin uses when the forum scrape breaks" (`:21`) — prose that
  assumes a watcher that was subsequently cut.

The owner's ruling (2026-08-06): **the source of truth for the game version is our API; the client
does not read lotro.com.**

## Decision

### 1. No client ever contacts lotro.com

`IForumPageFetcher`, `ForumPageFetcher` and the release-notes regex leave the patcher. Forum
scraping becomes a TMS-only concern. This covers the M4 Avalonia app by construction — it reuses
the same Application handlers.

### 2. The verdict travels on the distribution response

The client learns everything it needs from the response it already fetches
(`GET /api/v1/translation-files/{lang}`, anonymous). No second round-trip, no new anonymous hole
in the version catalog — `GET /api/v1/game-versions` keeps `RequireTranslatorRole`. The carrier
must survive **304**, since the steady state is a cache hit (ETag).

### 3. The server ships a verdict, not two versions to compare

The response carries the artifact's own `GameVersion` and a server-computed staleness verdict:
*is a newer game version known to the TMS that this artifact does not yet reflect?* The client
performs no version arithmetic — LOTRO notation ordering stays behind `LotroNotationVersion` in
the TMS domain, where it can be fixed by a deploy rather than by every player upgrading a CLI.

Three rules are normative, not implementation detail:

- **Only `Unprocessed` versions count toward the verdict.** A `Superseded` row is a dead
  registration, never evidence of a live update. A status-agnostic "max known version" reading
  would let one stale row disable Tier-0 repair for every player indefinitely.
- **Unknown means do not repair.** A missing artifact version, an absent verdict, an unparseable
  response, or an unreachable TMS ⇒ never force-patch, proceed to launch. This covers the deploy
  window where the artifact has no version yet (§ Implementation Notes: the column is nullable).
- **The verdict is response-scoped and never persisted as authority.** It is valid only for the
  response that carried it. A verdict cached on disk and replayed on an offline launch, or under
  `--skip-sync`, would assert freshness nobody vouched for.

The Tier-0 rule becomes: force-patch only when the server says the artifact is current. Spec
0012's "artifact-version ≥ live forum version" phrasing is superseded by this.

### 4. #85 (M2-18 forum watcher) is what gives the guard useful coverage — promoted into UR

The guard is not inert without #85, and an earlier draft of this ADR claimed it was. The manual
ceremony registers a version *before* importing into it — import is keyed by `GameVersionId`
(`ImportExportedTexts.cs:115`) and the admin UI forces picking an existing row
(`ImportExport.razor:133`) — so from registration until the import commits there is an
`Unprocessed` version newer than the artifact and the verdict correctly reads "stale".

What manual registration cannot cover is the window that matters most: from SSG publishing the
release notes until the admin notices. That is exactly when players update and lose translations,
and there the verdict reads "current" and the sentinel repairs with pre-update Polish. The watcher
moves the start of coverage from admin-notice to publication.

Therefore #85 moves from Post-MVP / Backlog into the UR — update-resilience milestone as a
dependency of #562 (UR-22) and #565 (UR-10), and ships before the sentinel reaches players. It
keeps the ADR-0030 §2 scope amendment (e-mail alert on detection), which was never mirrored onto
the ticket — do that when it is picked up.

This partially supersedes ADR-0030. ADR-0030 §1 (VM runner deferred), §2's other two moves and
§3's reconsider triggers stand unchanged. What no longer holds is #85's placement as post-MVP: a
client-facing guard now depends on how early the TMS learns about an update. The manual
export→import ceremony itself stays manual.

### 5. Detected versions are active on arrival, dismissible after the fact

The watcher's detections count toward the verdict immediately; there is no draft state awaiting
admin approval. Approval latency would leave the verdict "current" through precisely the
publication→approval window §4 exists to close, and the failure mode there is masking. The failure
mode of a wrong auto-active detection is the opposite — the sentinel declines to repair — which is
recoverable and never corrupts a player's game. The asymmetry decides it.

The ADR-0030 §2 e-mail alert is the review step, performed after the fact. The admin's lever is
dismissal of a bogus detection, not confirmation of a correct one, so the common path costs no
human action and only the rare path does.

Two constraints on this:

- **Dismissal must always be available** (see Prerequisite below). Auto-active detection is only
  safe while a bogus row can be retired at any time; today it cannot.
- **No silent monotonic filter.** Rejecting anything not strictly greater than the latest known
  version does not stop the detection that actually hurts — a Bullroarer/preview thread carries a
  *higher* version and sails through — while it silently drops legitimate versions under any
  string comparison (`48.10` sorts below `48.9`; `100` below `49`). Use a plausibility bound
  (reject not-strictly-greater, reject implausibly far ahead) that raises an alert instead of
  discarding quietly, and pin the comparator with tests against the ADR-0003 canonicalization.

### 6. Trust in a detection is a separate axis from the import lifecycle

Detection provenance (manual vs watcher) and dismissal do not belong in `GameVersionStatus`. That
enum models one question — has this version's export been imported — and every existing invariant
hangs off it (`EnsureCanBeDeleted` admits only `Unprocessed`, `MarkSuperseded` refuses `Processed`,
the import sweep queries `GetUnprocessedDetectedBeforeAsync`). A fourth state would thread through
all of them. Provenance and dismissal are orthogonal fields on the aggregate.

### 7. The preflight update check reads the TMS

`PreflightCheckQueryHandler` keeps its shape and `IGameUpdateChecker` keeps its name and seam;
only the implementation changes — it reads the TMS-served version instead of scraping (through the
entry point §8 gives it). Graceful degradation is preserved exactly: an unreachable server yields
no version and never blocks `patch`, matching today's `LogForumFetchFailed` path.

The client still performs no version ordering. Reporting that the TMS version differs from the one
stored in `version.txt` is a string inequality, which is what the check does today; deciding which
of two versions is newer stays in the TMS (§3).

### 8. Two client channels: headers on the launch path, a rel-resolved endpoint for preflight

Both start at the anonymous discovery root (`GET /`, `.AllowAnonymous()`); neither leaves a literal
API path in the patcher.

**Launch — the sentinel.** The distribution URL is resolved by the existing `Rels.TranslationFile`,
per ADR-0041 — that is #611's job, killing the hardcoded `api/v1/translation-files/pl` in
`TranslationFileDownloader.cs:27`. The artifact version and the verdict travel as response headers
on that same request, because they must survive a 304 and a header is not rel-addressable. No
second round-trip on a path that is already talking to the TMS.

**Patch — the preflight (§7).** That path has no request to ride on. `patch` downloads no artifact,
and it does not even hold a server address: `--tms-url` exists only on `LaunchCommand`
(`GlobalSettings.DefaultTmsBaseUrl` is a blank constant). Reading a header there would mean
fetching the whole artifact — ~82 MB in the same `||` format — to learn one scalar, and it would
only degrade to a cheap 304 when a cached ETag happens to exist. So the preflight gets its own
anonymous entry point, `GET /api/v1/game-versions/current`, advertised under a new rel
`current-game-version` (#626) and consumed by #627, which also puts `--tms-url` on `patch`.

The two channels answer different questions and must not be collapsed into one. The header verdict
is response-scoped and counts only `Unprocessed` versions (§3). The endpoint answers "what is the
newest version the TMS knows", every status included — to an admin standing in front of a DAT a
`Processed` version is still the current game version.

Rel names are a frozen public contract (ADR-0041): adding `current-game-version` is cheap, renaming
it after clients ship is not.

### 9. The forum regex ends up in exactly one place

ADR-0002 mandates that the two bounded contexts share no code, which is why #85 was specified to
*duplicate* the patcher's regex. After this ADR there is nothing to duplicate: the patcher drops
its copy and the TMS holds the only one. No code is shared — the version crosses the boundary over
HTTP, exactly as spec 0012's Assumptions require.

## Prerequisite defect — a bogus version can become permanently unusable

§5 is only safe once this is fixed, and it is reachable today from an admin typo alone:

1. Version `50` is registered by mistake (`Unprocessed`).
2. A later import of the real `49.2` sweeps every older unprocessed row into `Superseded`
   (`ImportExportedTexts.cs:321-333`).
3. Update 50 actually ships. Registration is refused — `ExistsByVersionAsync` ignores status
   (`GameVersionRepository.cs:47-52`). Import is refused — the `import` rel is withheld for
   `Superseded` (`GameVersionAggregateLinkFactory.cs:47`) and `MarkAsProcessed` rejects it
   (`GameVersion.cs:28-31`). Deletion is refused — `EnsureCanBeDeleted` admits only `Unprocessed`
   (`GameVersion.cs:58-66`).

`MarkSuperseded` has one call site and no inverse anywhere in `src/`. Canonicalization
(ADR-0003) means `50`, `50.0` and `50.0.0` are the same value, so there is no spelling around it.
The version number is dead: hand-written SQL, or a deliberately wrong row, are the only exits.

A watcher that registers automatically turns a rare typo into a recurring, unattended path into
that state. The recovery route must exist before detection goes auto-active.

## Consequences

### Positive

- One source of truth for "what version is the game", owned by the context that already models it.
- No lotro.com dependency anywhere in a shipped client. In the launch path this is prevention
  rather than removal — nothing there reaches the forum today; on the `patch` path it removes a
  live third-party call from the admin's box.
- Forum HTML breakage becomes a server-side deploy fix instead of a defect frozen into every
  installed CLI.
- The anti-masking guard becomes server-side testable — an integration test on the distribution
  endpoint, rather than an untestable comparison against live third-party HTML.
- The patcher sheds a fetcher implementation and a regex (the shared `HttpClient` singleton stays —
  `TranslationFileDownloader` uses it).
- `ForumPageFetcher`'s AUDIT-SEC-04 size cap and the AUDIT-SEC-07 regex timeout stop being
  client-side attack surface; the equivalent guards live in one server we control.
- A dead branch goes with it: `GameUpdateCheckSummary.ForumVersionChanged` / `.IsFirstLaunch` /
  `.ForumCheckSucceeded` have no production consumers, and the launch response hardcodes
  `ForumVersion: null` (`SimplifiedGameLaunchingStrategy.cs:175`), so the scrape's only effect
  today is a field written to `version.txt` and read back only to be rewritten.

### Negative / Accepted Trade-offs

- The guard is only as fresh as the watcher. A silently broken scrape (forum markup change)
  degrades the verdict to "always current" — the exact failure §4 exists to narrow. The watcher
  needs a visible liveness signal, not just a success log; treat "no successful scrape in N hours"
  as an alertable state when #85 is built.
- #85 becomes pre-launch work that ADR-0030 had deliberately parked, and it adds a hosted service
  plus SMTP to the TMS.
- The guard covers the forced repair only. The routine launch path patches whenever the artifact
  hash changed (`SimplifiedGameLaunchingStrategy.cs:85-99`), with no version gate, so an unrelated
  approve that rebuilds the artifact mid-window still re-injects pre-update Polish. Accepted for
  now — closing it means gating the ordinary patch on the verdict too, trading a masking risk for
  a wider "no Polish at all" window. Revisit if a real update day shows it hurting.
- TMS unreachable ⇒ no repair. Tier 0 declines rather than masks, but also never heals a
  collateral revert while offline. Accepted: masking is the worse failure.
- The distribution response grows contract surface that must hold on 304 — a header dropped on the
  cache-hit path silently disables the guard. Pin it with a test at both ends.
- A patcher change on a component whose bar is "stable" (ADR-0002 amendment). Existing assertions
  stay untouched, but ~23 tests are deleted with their subject: `GameUpdateCheckerTests` (18) and
  `ForumPageFetcherTests` (5) test the scrape itself. `PreflightCheckQueryHandlerTests` stubs the
  interface and survives.

## Alternatives Considered

### A. Client scrapes lotro.com per launch (spec 0012 as written)

The straightforward reading of the handshake, and the scrape code already exists. **Rejected** —
owner decision. It also multiplies a brittle third-party HTML dependency across every installed
client with no hotfix path, moves the guard's correctness outside anything we can test or deploy,
and puts an unrequested outbound call in the launch path.

### B. Client queries `GET /api/v1/game-versions`

**Rejected.** It requires the translator role (`ListGameVersions.cs:109`); making it anonymous
would expose the admin's operational version catalog to buy one scalar, and it costs a second
round-trip on a path that is already talking to the TMS.

Both objections are about **that** endpoint on **the launch path**, and neither carries over to the
preflight (§8). There, nothing is already in flight to piggyback on, and the answer comes from a
purpose-built single-value endpoint that returns one version plus its status — a number SSG
publishes itself — instead of the catalog. `GET /api/v1/game-versions` and `/{id}` keep
`RequireTranslatorRole` either way.

### C. Client derives the version locally (DAT vnum or launcher files)

No network at all. **Rejected.** DAT vnum is empirically dead as a content version (unchanged
across six update cycles, `docs/knowledge-base/`), and any launcher-file signal is unproven — it
would need its own experiment before it could carry a correctness guard.

### D. Server sends both versions, client compares them

Simplest server change. **Rejected.** It ships LOTRO version-notation ordering into a binary we
cannot redeploy, duplicating a rule `LotroNotationVersion` already owns. The server knows the
answer; sending the question instead is strictly worse.

### E. Ship UR without #85 — manual registration only, done early

The honest version of this alternative: the admin registers the new version from a phone the hour
they hear about the update, days before they can get to a Windows box to export. Registration is
already decoupled from import, so this closes much of the same window with zero new infrastructure.
Rejected (owner, 2026-08-06) as the primary mechanism — it makes a client-facing guard depend on a
human noticing an announcement, and the bus factor ADR-0030 §4 names is exactly that human. Kept as
the standing fallback whenever the watcher is down: registering early is always the right move.

### F. Draft detections requiring admin approval before they count

The instinct behind it is right — a scraped title is not authority. Rejected on latency: approval
sits in front of the publication→player-launch window, which is measured in hours and is the whole
reason the guard exists. §5 keeps the review but moves it after the fact, where its cost is paid
only by wrong detections.

## Implementation Notes

- TMS — artifact gains a version (#562/UR-22, materially bigger than its ticket says):
  `Projections/PrecomputedTranslationFile.cs` plus its EF configuration and a forward-only
  migration (ADR-0023) — the column must be nullable, since existing rows have no version and the
  insert path runs through an immutable constructor; `PrecomputedTranslationFileStore.TryRefreshAsync`
  is a set-based `ExecuteUpdate` with an explicit column list (`:29-37`) and silently will not
  refresh a column that is not added there; `PrecomputedTranslationFileProjector` must learn which
  version it is generating for; `Features/TranslationFiles/GetTranslationFile.cs` emits version +
  verdict headers on 200 and 304.
- TMS — the verdict (#562): derived from "an `Unprocessed` version greater than the artifact's
  exists" (§3). Needs a comparator on `LotroNotationVersion` — none exists today; it must be
  segment-wise numeric over the canonical form, because ordinal string comparison puts `49.10`
  below `49.9` and `100` below `49`.
- TMS — the verdict's source (#85/M2-18): hosted watcher in `TranslationSystem.API` creating
  `Unprocessed` rows through the existing aggregate, with provenance + dismissal per §5/§6, the
  ADR-0030 §2 e-mail alert, and a liveness signal.
- TMS — recovery (Prerequisite): a bogus registration must stay retirable after it has been
  superseded, and a superseded number must not block registering the real version.
- TMS — the preflight's entry point (#626): an anonymous `GET /api/v1/game-versions/current`
  returning the numerically newest known version with its status, plus the `current-game-version`
  rel in `Rels` and `DiscoveryLinkFactory`. It orders by the same `LotroNotationVersion` comparator
  #562 introduces — `DetectedAt` would let a late registration of a lower number win.
- Patcher — the whole de-scrape is #627, and it depends on #626 and #611 being on the wire first.
- Patcher — remove: `Application/Abstractions/IForumPageFetcher.cs`,
  `Infrastructure/Network/ForumPageFetcher.cs`, the regex in
  `Features/UpdateChecking/GameUpdateChecker.cs`, and the fetcher's DI line
  (`InfrastructureDependencyInjection.cs:42`). Keep the `IGameUpdateChecker` registration
  (`ApplicationDependencyInjection.cs:36`) — §7 retains the seam and swaps only the implementation.
- Patcher — rewire: `GameUpdateChecker` reads the TMS-served version; `PreflightCheckQueryHandler`
  unchanged in shape; the sentinel consumes the verdict from the live response, not from disk (§3),
  and note that the CLI's 304 branch returns before touching the cache
  (`SyncTranslationFileCommandHandler.cs:62`) — the artifact version needs its own persistence path
  if the sentinel is to know it after a cache hit;
  `Infrastructure/Network/TranslationFileDownloader.cs:27` loses its hardcoded route via #611.
- Docs to realign: spec 0012 (Tier-0 handshake wording + AC item 2), the stale XML doc on
  `Features/GameVersions/RegisterGameVersion.cs:21`, #562's false premise about the artifact
  already knowing its version, #565's client-side handshake tasks, and #85 (milestone UR, the
  regex-duplication line, and the ADR-0030 §2 e-mail scope).
- Tests: integration on the distribution endpoint asserting version + verdict survive a 304; unit
  tests on the sentinel's branches (current / stale / unknown); a comparator suite pinning
  `48.10 > 48.9`, `100 > 49` and canonical equality; an integration test proving a superseded
  bogus version can still be retired and its number reused.

## References

- `docs/specs/0012-update-resilience.md` — Tier 0 handshake, Q1–Q5, AC (amended here)
- `docs/specs/0001-game-update-lifecycle-and-translation-invalidation.md` — GameVersion lifecycle
- ADR-0030 — manual export ceremony (partially superseded: #85's post-MVP placement only)
- ADR-0041 — no API gateway; clients resolve entry points by rel
- ADR-0002 — bounded contexts share no code (the duplication argument narrows, §7)
- ADR-0023 — forward-only, N-1 compatible migrations (the artifact's new column)
- `docs/knowledge-base/` — DAT vnum useless as a content version; per-SubFile survival model
- Tickets #85 (UR-24 forum watcher), #562 (UR-22 artifact version + verdict), #565 (UR-10 sentinel),
  #611 (CLI discovery), #624 (UR-23 superseded rows stay retirable), #626 (UR-25 current-version
  endpoint + rel), #627 (UR-26 patcher drops the scrape)
