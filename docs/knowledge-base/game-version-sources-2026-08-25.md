# Where the game version comes from — and where it does not (2026-08-25)

**Status:** settled for every source we could reach from a Mac. Two local candidates remain
unchecked (see "Still open") and need one look on a Windows box with LOTRO installed.

## The question

Is there a deterministic way to read the LOTRO content version (`49.4`) from anything on the
player's machine, or from any API — instead of scraping the release-notes forum?

## The answer

No local source exists. The game client does not know its own "49.4"; that number lives only in
SSG's announcements. There is a better channel for the announcement than the forum HTML: the Steam
News API.

## What was checked

### The launcher's "update available" beacon

Reconstructed from OneLauncher (open-source LOTRO/DDO launcher), `Xaymar/RE-DDO` (reverse-engineered
DDO launcher protocol, same Turbine engine) and live requests:

1. The launcher calls SOAP `GetDatacenters` on `http://gls.lotro.com/GLS.DataCenterServer/Service.asmx`.
   The reply carries `PatchServer = patch.lotro.com:6015` and
   `LauncherConfigurationServer = http://gls.lotro.com/launcher/lotro/lotrolauncher.server.config.xml`.
2. That XML has a `Game.Version` key. Live value on 2026-08-25: **`3601.0066.7272.4024`**.
   The Wayback snapshot of 2025-08-29 has the **same value** — a year and roughly ten updates
   (two majors) later. It is the installer image version (OneLauncher uses it only to build the
   Akamai download path). Dead as a content signal, exactly like DAT vnum. Cross-check: DDO's value
   moved from `2600.0047.2096.4145` (2019) to `5000.0050.3264.4021` (2025) — it changes when the
   installer image is refreshed, not per update.
3. The real "you need to patch" comes from `patchclient.dll` talking a binary, undocumented
   protocol to `patch.lotro.com:6015` (`POST /stateless2`). It compares per-file version stamps in
   the DATs (log lines: "GUID version stamp", "Extent stamp mismatch"). The result is "these files
   differ", never a version number.

So the launcher never sees "49.4" and does not need it.

### LOTRO Companion

The maintainer (dadoo) tags data releases by hand — SourceForge folders like `patchs/On24.4`. There
is no version read from the game files. Watching lotro-data (#384, now closed) would watch one
hobbyist's reaction time, not a signal.

### DAT internals we already read

`DatExportNative` exposes vnum (schema version, dead — [vnum-observations.md](vnum-observations.md))
and per-SubFile `version` + `iteration` (E5, [update-49/RESULTS.md](update-49/RESULTS.md)). The
per-SubFile stamps are a **fingerprint of content**, not a label. They are the deterministic local
identity of a DAT state, and the natural candidate for stamping `exported.txt` with what it was
exported from — see "Design consequence".

### APIs that do carry the announcement

- **Steam News API** — `https://api.steampowered.com/ISteamNews/GetNewsForApp/v2/?appid=212500&count=15`.
  Official Valve endpoint, JSON, no key, unix timestamps, full history. SSG posts every
  "Update X Release Notes" there in parallel with the forum. Verified live: titles from
  "Update 48 Release Notes" to "Update 49.4 Release Notes", in order.
- Forum RSS — `https://forums.lotro.com/index.php?forums/release-notes-and-known-issues.7/index.rss`.
  Works, XML, titles only. Fallback, never the HTML (the forum moved vBulletin → XenForo and every
  old thread link is dead).

The one shared weakness: the title is typed by an SSG employee. The regex
`Update\s+(\d+(?:\.\d+)*)\s+Release\s+Notes` stays, and it is the only fragile piece left.

## Still open (Windows, owner-run, two minutes)

```powershell
(Get-Item "$env:LOTRO\lotroclient64.exe").VersionInfo | fl FileVersion,ProductVersion
Select-String -Path "$env:LOCALAPPDATA\The Lord of the Rings Online\*.txt","$env:LOCALAPPDATA\The Lord of the Rings Online\LotroLauncher*.log" -Pattern "49\.4" | select -First 20
```

Expected: build numbers in the `3601.66.…` style, nothing that says `49.4`. The real test is
differential — the same search right after the next update; a file that went `49.4` → `49.5` is a
candidate, a file that kept `49.4` is noise. Note that `LotroLauncher.log` "Product Token" lines are
owned expansions, not the client version (a forum thread often quoted for this is about purchases).

## Design consequence

- The version number is a human label attached to an export, not something the file can prove.
  A `GameVersion` row exists so that the assumption "this file is from this game" is said out loud
  by the admin at import time, instead of being silent. That is why registration stays manual: the
  same person sees the announcement, registers the version and makes the export.
- `exported.txt` carries no version. If it ever should, the honest stamp is the per-SubFile
  fingerprint (deterministic, no forum, no race), with the version staying a label. A forum-derived
  stamp at export time would race the launcher (post before/after the client patched) and must not
  be treated as proof.
- Player safety does not depend on any of this: the per-row `source_digest` guard (ADR-0047) skips
  a row whose English changed, with or without an import. The watcher (#85) only shortens the
  window of wasted translator work and empty coverage after an update.

## Decisions recorded

- #85: the watcher reads the Steam News API, not the forum HTML (owner, 2026-08-25).
- #384: closed — lotro-data is not a signal.
