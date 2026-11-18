# Spec 0016: Desktop app installer and self-update

- **Status:** Implemented
- **Date:** 2026-11-16
- **Author:** koniecdev
- **Ticket:** #45 (M4-05)
- **Amended:** 2026-11-18 by #1464 (M4-13): admin rights at start, every file of the app under its
  own folder, one running copy. See "Where the app keeps its files".
- **Related:** ADR-0070 (amended 2026-11-16 for desktop releases), ADR-0033 (the two desktop
  projects), ADR-0067 §7 (GitHub is outside our trust boundary), ADR-0069 (amended 2026-11-18 for
  the app's folders), wiki `Wersje-systemu`, #42 (the Patch and Play buttons), #43 (settings), #1443
  (signing), #1321 (moves the
  patcher's network adapters to a cross-platform project). Sibling slice to mirror:
  `Application/Features/TranslationFileSyncing/`.

## Business context

A player must install one program and play in Polish, with no .NET to install and no `.bat` files
(wiki `Wersje-systemu`, 1.0 point 1). The desktop app (M4) is that program, but today it is only a
single exe that a player has to copy by hand, and nothing tells them when a new version exists.
The owner's decisions on #45 (2026-11-16, two `Owner decision` comments) fix how it ships and how
it updates.

## Goal

A player installs the app with a normal Windows installer and removes it from Windows settings.
When a newer version is out, the app offers it at start and installs it after one click.

## In scope

- An Inno Setup installer, in Polish, for all users, with a desktop shortcut, a Start Menu entry and
  an uninstaller.
- A GitHub Actions workflow that builds, tests and publishes a desktop release from "Run workflow"
  on `main`, and runs the same build and install test on pull requests that touch the app.
- The update check at start and the update itself (download, SHA-256 check, run the installer).
- The ADR-0070 amendment, the runbook section "Cutting a desktop release", the CLAUDE.md lines and
  one sentence in the wiki page `Wersje-systemu`.

## Out of scope

- Code signing: #1443. Until then Windows SmartScreen warns about an unknown publisher.
- Admin rights at start, the native libraries next to the exe and a download folder only admins
  can write: #1464 added them later (see "Where the app keeps its files"). Every copy now runs as
  admin, so an update shows no UAC prompt.
- The Patch and Play buttons and their use of the shared busy gate: #42 (a comment on #42 names the
  gate).
- Restoring English game files on uninstall: no such feature exists, and the uninstaller never
  touches the game folder.

## Business rules & edge cases

### Version numbers

- The desktop app has its own numbers, separate from the server's `vX.Y.Z` releases. A desktop
  release is the tag `desktop-vX.Y.Z` plus a GitHub Release with the same name. The first one is
  `desktop-v0.1.0`.
- **Grammar** (one parser for the tag and for the version the app was built with):
  `^(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})$`. No leading zeros, no
  pre-release suffix, each part at most nine digits so it fits an `int`. Versions compare part by
  part.
- **0.0.0 is a developer build.** A build that was not stamped by the release workflow (a local
  build, a PR run) carries 0.0.0 and never checks for updates. A value that does not parse counts as
  0.0.0. The release workflow refuses to publish 0.0.0.
- The exe carries its version as the assembly metadata `DesktopVersion`, set from `$(Version)`. The
  csproj sets `<Version>0.0.0</Version>`; the release workflow overrides it with `-p:Version=X.Y.Z`.

### Release assets (a contract every shipped app keeps)

- `LotroPoPolsku-Setup-X.Y.Z.exe`: the installer.
- `LotroPoPolsku-Setup-X.Y.Z.exe.sha256`: one line, in `sha256sum` format: 64 lower-case hex
  characters, two spaces, the installer's file name, a line feed. ASCII, no byte order mark. The app
  accepts upper-case hex too, ignores a leading `*` before the name (binary mode) and trailing
  whitespace, and refuses a file that names another file.
- A release that lacks either asset is never offered.

### Release workflow (`.github/workflows/desktop-release.yml`)

- **Trigger:** `workflow_dispatch` with the inputs `version` (required) and `notes` (optional), and
  `pull_request` on the paths that build into the installer. No workflow starts on a tag, a release
  or a created ref (ADR-0070; `scripts/tests/commit-image.tests.sh` enforces it).
- **Refusals before any build** (dispatch only): the ref is not exactly `refs/heads/main`; the
  version does not match the grammar; the version is 0.0.0; the tag `desktop-vX.Y.Z` exists; the
  version is not higher than the highest existing `desktop-v` tag. `scripts/ci/desktop-release-check.sh`
  makes these checks, and `scripts/tests/desktop-release-check.tests.sh` tests each one in
  `pr-verify` and `ci`. Inputs reach scripts only through `env:`.
- **Build and smoke test** on `windows-2025`: publish the exe with `-p:Version=X.Y.Z
  -p:DebugType=embedded`, build the installer with the image's Inno Setup 6 (called by its real
  path), install it with the app's update flags, check that the installed exe carries version X.Y.Z,
  check the app's folders (#1464), install it a second time over the running app, start a second
  copy (it must show its message and end), try a silent uninstall while the app runs (it must
  refuse), uninstall it, and check what each step left behind (the AC list below). The SHA-256 file
  is written on this job, from the very file it installed.
- **Publish:** only after the smoke test, a job with `contents: write` runs the version check again
  (so a re-run of that job alone refuses a version that is no longer the next one), then creates the
  ref `refs/tags/desktop-vX.Y.Z` on the dispatched commit through the git refs API (GitHub refuses
  an existing ref, so two runs cannot both win), then `gh release create --verify-tag
  --latest=false` with both files. The release is published at once, with no draft (owner decision).
  If the release step fails or is cancelled after the tag was made, the job deletes the tag it made,
  and a draft release with it (`scripts/ci/desktop-release-cleanup.sh`, with a stub-`gh` self-test
  that pr-verify and CI run). A release that already went public stays, with its tag: apps may
  already have taken its file. When the job cannot ask GitHub about the release, or gets an answer
  it does not expect, it keeps the tag and warns; the runbook says how to check it by hand.
- **Not Latest:** a desktop release is never marked Latest. GitHub's "Latest" stays on the server's
  releases, and the server's release step passes `--latest` explicitly. The app never reads Latest.
- **Pull request leg:** the same build and smoke test, with version 0.0.0 and `contents: read`. It
  is not a required check. It is the only proof of the workflow before it reaches `main`, and the
  first CI run that starts the desktop app (and so its composition root with `ValidateOnBuild`).

### Installer (`installer/LotroPoPolsku.iss`)

- Fixed `AppId` `{53881929-2972-43D8-ACE2-1AED0D822D9F}`. It ties every later version to the same
  install, so never change it.
- `PrivilegesRequired=admin`, installed for all users into `{autopf}\LotroPoPolsku` (Program Files
  (x86), because the app is x86). Polish wizard only. No folder page (`DisableDirPage=yes`, #1464):
  the app's own files are safe only under Program Files, and a folder such as `C:\Games` inherits
  write rights for every user from the drive root. Never a per-user install
  (`PrivilegesRequiredOverridesAllowed`).
- `MinVersion=10.0`: Microsoft supports .NET 10 on client Windows from Windows 10 on, so Setup
  refuses an older Windows.
- Files: the publish folder without `*.pdb` and without a root `data` or `updates` folder, plus
  `LICENSE` and `THIRD-PARTY-NOTICES.md` from the repo root (`NOTICE` is already in the publish
  folder, spec 0011 LEGAL-09 and LEGAL-12). No DLL is listed by name: the publish folder holds the
  native DLLs next to the single-file exe (`datexport.dll`, its C runtime, Skia, HarfBuzz, ANGLE;
  `IncludeNativeLibrariesForSelfExtract=false`, #1464).
- A desktop shortcut and a Start Menu entry, always (an update puts back a deleted shortcut).
- `CloseApplications=yes`, `RestartApplications=no`, no `AppMutex`. The `[Run]` entry starts the
  app at the end with `postinstall nowait runascurrentuser`: checked in a normal install, run on its
  own in a silent one, started from the elevated installer so no second prompt and no error 740
  for an app that asks for admin rights, and `nowait` so the installer does not wait for the app to
  close.
- **Uninstall while the app runs:** the uninstaller cannot remove a running exe, so it asks in
  Polish to close the app first (OK checks again, Cancel stops the uninstall). The app holds the
  named mutex `Global\LotroPoPolsku_Running` while it runs, and `LotroPoPolsku_Running` beside it,
  which matters only when Windows refuses the global one. The `[Code]` section checks both in
  `InitializeUninstall`; `CheckForMutexes` is true when either exists. A silent uninstall cannot ask,
  so it stops and removes nothing. This is not an `AppMutex` directive: that one would also stop a
  silent update, which closes the app itself. A mutex the app may not open counts as a running
  copy. A name another kind of object holds never stops the app; the app logs it as a Warning.
  Every copy runs as admin (#1464), so an
  uninstaller run by any admin account normally sees the mutex. Unlike the app, Inno Setup's
  `CheckForMutexes` counts a mutex it may not open as no mutex, so in that case the uninstaller
  goes on and leaves the running exe behind. A second copy that only shows its "already
  running" message holds no mutex by design. If the first copy has ended while that message is
  open, the uninstaller does not see that copy: it removes the rest and leaves that exe behind,
  and the player deletes it by hand.
- **Uninstall removes only what the installer put in** (owner decision): the program files, both
  shortcuts and the uninstall entry. `{app}\data` (the settings, the log, and from #42 the downloaded
  `polish.txt` and its `.ledger`, ADR-0047 §4) and `{app}\updates` stay, so the install folder stays
  too. The game folder is never touched, so the game stays Polish until the LOTRO launcher repairs
  its files.

### Where the app keeps its files (#1464)

- The app asks for admin rights once, at start (`requireAdministrator`, owner decision 2 on #42),
  and never starts itself again with them.
- Everything the app keeps for itself sits under the exe's folder (`AppContext.BaseDirectory`),
  which only administrators can change once it is installed under Program Files (ADR-0069
  amendment of 2026-11-18):
  - `data\config.json` and `data\LotroPoPolsku.log`;
  - `updates\<guid>\`, one per downloaded installer, and `updates\update-install.log`;
  - from #42 on, the downloaded translation file and its sidecars, in `data\`.

  Nothing of the app's goes to the player's `%AppData%` or `%TEMP%`, and the exe unpacks nothing at
  start. A developer build uses its own `bin` folder by the same rule.
- **One copy per PC.** A second start shows a message box with the caption "LOTRO po Polsku już
  działa" and the text "LOTRO po Polsku jest już uruchomione. Jego okno może być zminimalizowane
  albo otwarte na innym koncie Windows na tym komputerze.", and ends. The first copy keeps running. The
  `Global\` mutex decides, so a copy in another Windows account counts: they share one install, one
  `data\` and one game. A global mutex the app may not open counts as such a copy. The plain name
  decides only when another kind of object holds the global name, and when both names are held that
  way the app starts anyway.
- **Per PC, not per account.** Every Windows account on the PC shares the settings and the log, and
  can read them. Before, each admin account had its own. Editing `config.json` by hand needs an
  editor started as administrator.
- `data\` is not a place for a DAT. The locator's `data\client_local_English.dat` probe resolves
  against the working directory, which the app sets to its own folder at start, whoever starts it.
  Only an admin can put a file there, and that source still needs the player's confirmation
  (ADR-0069).
- No migration: no desktop release was ever published, so nothing is read from the old
  `%AppData%\LotroPoPolsku`.

### Update check (at start)

- It runs in the background after the main window opens. It never blocks Patch or Play and never
  takes the busy gate.
- Old update folders under `{app}\updates` are deleted first; the installer log in that folder
  stays. A failure there is ignored.
- A 0.0.0 build stops here, with nothing shown.
- It calls `GET https://api.github.com/repos/koniecdev/LotroKoniecDev/releases?per_page=100`, with
  the GitHub JSON `Accept` header and no pinned `X-GitHub-Api-Version`: GitHub answers `410 Gone` to
  a retired version, which would end updates for good in every app that still sends it. A request
  without the header gets GitHub's default version, and the five fields the app reads are the same
  in every version. It never uses `/releases/latest`, because
  "Latest" is shared with the server's releases (owner decision).
- It picks the highest version among releases that are not drafts or prereleases, whose tag is
  `desktop-v` plus a valid version, and that carry both assets with their exact names and with
  download URLs equal to
  `https://github.com/koniecdev/LotroKoniecDev/releases/download/desktop-vX.Y.Z/<asset name>`.
- Newer than the app → the panel asks "Dostępna jest wersja X.Y.Z. Zaktualizować?" with "Tak" and
  "Nie". The same or older → nothing is shown. No desktop release among the 100 → the check failed
  (never "up to date").
- The whole check has 10 seconds. A failure (offline, a timeout, GitHub's limit of 60 anonymous
  requests an hour, an error status such as a 404 or a 5xx, an answer too large or not readable)
  shows a short Polish reason in the panel's status line, and the app keeps running on its
  version.

### Update ("Tak")

- It takes the busy gate, so it cannot run during a patch, and a patch cannot start during it.
- It reads the `.sha256` into memory (at most 1 KiB), then downloads the installer into a fresh
  folder `{app}\updates\<guid>` (at most 256 MiB, at most 10 minutes, progress on the
  shared progress bar). The 10 minutes cover the whole download, not only a silence: a line too slow to
  finish in that time (below about 0.5 Mbit/s for an installer of about 35 MB) fails with the
  timeout reason every time, and that player installs the new version by hand from the release
  page.
- **GitHub hosts only:** https on port 443, no user name or password in the URL, and the host is
  `github.com`, `api.github.com` or ends in `.githubusercontent.com`. The rule is checked on the
  address we ask for and again on the final address after redirects, before any byte of the body is
  read; a redirect that comes back unfollowed (a 3xx, such as https to http) is refused. The suffix
  rule, not a fixed list, because this update path is the only way an installed app can repair
  itself: a fixed list would lock out every installed app the day GitHub moves its download host.
  The `.sha256` and the installer both come from the same content host, after the same redirect
  from `github.com` over TLS. So the wider rule trusts no one beyond GitHub itself: whoever could
  send the redirect elsewhere could change both files.
- The app opens the downloaded installer once, read-only, sharing only read access, hashes it
  through that handle and keeps the handle until Windows has started the installer. A hash mismatch
  is refused.
- It starts the installer with the `runas` verb (`Verb = "runas"`, `UseShellExecute = true`) and
  exactly these arguments:
  `/SILENT /NOCANCEL /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /FORCECLOSEAPPLICATIONS "/LOG=<log>"`,
  where `<log>` is `{app}\updates\update-install.log` and its folder is created first.
  The arguments are one pinned string, never `ArgumentList`: .NET escapes an inner quote as `\"`,
  which Inno does not read. `/NOCANCEL` hides Setup's Cancel button: the app has closed by then, so
  a cancel would leave the player with no app running. `/FORCECLOSEAPPLICATIONS` relies on the gate,
  which keeps a patch in this copy of the app from running. A second start ends (#1464), unless
  another program holds the mutex name as another kind of object. Then the app closes; the
  installer starts the new version at the end.
- **Why `runas`:** Inno Setup's installer asks for no admin rights when it starts. It starts itself a
  second time, with them, through Windows' own `runas` (Inno Setup 6.7.1 source:
  `SetupLdrAndSetup.XPTheme-x86.manifest` is `asInvoker`; `Setup.SpawnServer.pas`). Started plainly
  from an app without admin rights, that second start and its prompt came after the app had closed,
  so a "No" left the player with no app running. With `runas` the prompt comes from the app's own
  start, and a "No" comes back to the app as Win32 error 1223. An installer that already has admin
  rights does not start itself again. Every copy runs as admin since #1464, so it gets no prompt,
  and the "No" path is not reached on Windows. The code keeps the `runas` start and its "No": they
  cost nothing and their unit tests stay.
- **What the held handle covers:** Windows creates the installer process, with admin rights, from
  the held file, so the program that starts is the file that was hashed, and no other program can
  write, delete or rename the file until then. Inno Setup then reads the same file again by path to
  unpack its setup data, partly after the app has closed. The hold does not cover those reads (see
  Assumptions).
- Any failure (a mismatch, a refused host, a limit, a network error, a "No" at the Windows admin
  prompt) starts nothing, releases the gate, shows a Polish reason and keeps the app running, with
  "Tak" usable again. It also deletes that update's download folder once the held file is released,
  so a refused file does not stay under the installer's own name (a folder that cannot go now goes
  at the next start).
- "Nie" hides the panel until the next start.

### Logging (#977)

A failed or slow check and a failed download are Information. A hash mismatch, a SHA-256 file that
does not hold one hash for the installer, and a refused host are Warning: they should never happen
and may be an attack. A file over its size limit stays Information: the limits protect the app, and
a bigger installer can be a real release.

### Where the code lives

ADR-0033's 2026-10-21 amendment says Windows-only code lives in the exe, behind an interface the UI
injects; §2 says the Windows bits live behind abstractions. Read together: the UI project holds
nothing Windows-only, and the Windows parts sit in the exe's Windows-only graph behind a port. So
the use case is a patcher Application slice (`Features/AppUpdating/`, tested on Linux), and the
adapters sit in the patcher Infrastructure: the GitHub client in `Network/` (it reuses the internal
`BoundedResponseReader` and the shared client) and the download folder in `AppUpdating/`, both in
the cross-platform project. The installer start sits in `AppUpdating/` of
`LotroKoniecDev.Infrastructure.Windows` next to `GameLauncher`, because it starts a Windows program
(#1321, ADR-0033's 2026-11-18 amendment). The CLI's graph registers these too and validates them; it
never calls them.

## Contract

- **Trigger:** the desktop app's main window opens; the player clicks "Tak".
- **Input:** `CheckForAppUpdateQuery(string InstalledVersion)`;
  `InstallAppUpdateCommand(DesktopVersion Version, Uri InstallerUri, Uri ChecksumUri)`.
- **Output:** `AppUpdateCheckResponse` (outcome `DevelopmentBuild`, `UpToDate`, `UpdateAvailable` with the
  version and both URLs, or `CheckFailed` with the error); `Result` for the install.
- **Errors:** `DomainErrors.AppUpdate.*`, each mapped to a Polish reason in the panel.
- **Files touched:** `{app}\updates\<guid>\` (the downloaded installer),
  `{app}\updates\update-install.log` (the installer's log). Never the game folder.

## Acceptance criteria

- [ ] A dispatch refuses, before any build: a ref other than `refs/heads/main`, a version off the
      grammar, 0.0.0, an existing `desktop-vX.Y.Z` tag, a version not above the highest
      `desktop-v` tag.
- [ ] A valid dispatch publishes the exe stamped X.Y.Z with a .NET 10 SDK (any other SDK fails the
      run), builds the installer with Inno Setup 6, and
      on the runner: install 1 exits 0 and leaves the exe with `NOTICE`, `LICENSE` and
      `THIRD-PARTY-NOTICES.md` and no `*.pdb`, both shortcuts, the uninstall key, and an app process
      that is still alive after 10 s; install 2 over the running app exits 0 and leaves a new app
      process; the uninstall removes what Setup installed, both shortcuts and the key, and leaves
      only `data\` (and `updates\`) in the install folder, with a marker file in `{app}\data`.
- [ ] After install 1 (#1464): the installed exe asks for `requireAdministrator`; the app stays
      alive with `DOTNET_STARTUP_HOOKS` pointing at a file that does not exist, so it loads no
      startup hook; the app's log is in `{app}\data`; nothing of the app's is in the
      player's `%APPDATA%` or `%TEMP%`, including the `.net` unpack folder; every native DLL of the
      publish folder sits next to the exe; the player, Users, Authenticated Users, Everyone and
      INTERACTIVE have no write right on `{app}` or `{app}\data`, whose owner is Administrators,
      SYSTEM or TrustedInstaller; no copy shows the "already running" message.
- [ ] After install 2, a second start from another folder shows the "already running" message,
      does not keep that folder as its working folder, and ends with exit code 0 once the message
      is closed; the first copy keeps running.
- [ ] Only then the tag `desktop-vX.Y.Z` names the dispatched commit and a published release, not
      Latest, carries the installer and a `.sha256` that `sha256sum -c` accepts.
- [ ] No workflow starts on a tag, a release or a created ref; `commit-image.tests.sh` is unchanged
      and green; actionlint passes.
- [ ] The build and smoke test run on a pull request that touches the app's inputs, with version
      0.0.0 and no release.
- [ ] A 0.0.0 build shows no panel and makes no request.
- [ ] The check picks the highest valid desktop release with both assets, and ignores drafts,
      prereleases, server releases, bad tags, missing or misnamed assets and foreign download URLs.
- [ ] A newer release shows "Dostępna jest wersja X.Y.Z. Zaktualizować?"; the same or an older one
      shows nothing; no desktop release among the 100 is a failed check.
- [ ] A failed or slow check shows a Polish reason and leaves Patch and Play as they were.
- [ ] "Tak" refuses a hash mismatch, a host outside GitHub, an unfollowed redirect, a file over its
      limit and a broken `.sha256`, starts nothing then, and shows a Polish reason.
- [ ] On a match, the installer starts with the `runas` verb and exactly the pinned argument string,
      and the app closes.
- [ ] A "No" at the Windows admin prompt starts nothing, releases the gate, shows "nie zgodzono się
      na uruchomienie instalatora" and leaves "Tak" usable.
- [ ] "Tak" is disabled while the gate is busy.
- [ ] "Nie" hides the panel.
- [ ] A silent uninstall while the app runs refuses and removes nothing; an interactive one asks to
      close the app first.

## Open questions

None. The owner answered the business questions on #45 (2026-11-16).

## Assumptions

- Only the first 100 releases are read. Server releases are rare bookmarks, so the newest desktop
  release is among them for years.
- The GitHub API allows 60 anonymous requests an hour per IP address. Players behind one shared
  address may hit it; the check then fails with a reason and tries again at the next start.
- The full update path against real GitHub is first proven by the step from `desktop-v0.1.0` to
  `desktop-v0.1.1`. Do not announce 0.1.0 to players before that.
- No green-CI check before a release: `ci.yml` skips docs-only commits and cancels older runs, so
  the tip of `main` often has no run of its own. The runbook says to look at `main`'s CI before
  pressing "Run workflow".
- #1464 closed the five same-user paths the notes on #42 listed (the native libraries unpacked
  into `%TEMP%`, a library beside the downloaded installer, the installer's log, the installer file
  that Inno Setup reads again by path, and the update folders the clean-up deletes by path): all of
  them now sit under `{app}`, inside Program Files, which a program running as the player cannot
  change. The held handle still covers only the start, but nobody without admin rights can swap the
  file after it. What stays open is in ADR-0069's amendment of 2026-11-18, item 6 (the runtime's
  environment variables, a non-default owner policy), handed to #1456.
- The busy gate is one object per running copy of the app, and a second start ends (#1464). When
  another program holds the mutex name as another kind of object, two copies can run, and from #42
  on an update in one can force-close a patch in the other. That needs a program that already runs
  on the PC, and is accepted.
- A standard user who elevates with an admin's password runs the app as that admin. The app's files
  are per PC, so the settings are the same for both.

## Alternatives considered

- **A plain start, with no verb** (the first version): the prompt came from Inno Setup's own second
  start, after the app had closed (see "Update").
- **Keep the handle until Inno Setup closes the app:** Inno Setup reads the installer again at
  install time, after the app has closed, so a gap stays.
- **A download folder only admins can write:** it removes the swap instead of handling it. Taken in
  #1464, as `{app}\updates`, once the app runs as admin.
- **Code signing (#1443):** it tells the player who made the file and quiets SmartScreen, but does
  not stop a read by path of a swapped file.
- **The newest release through `matching-refs` plus `/releases/tags/{tag}`:** safe from the
  100-release window, but two requests under the 60-an-hour limit, and the owner decision says that
  the check lists the releases.
- **MSIX, an in-place exe swap, a per-user installer (such as Velopack):** ruled out by the owner
  decisions (Inno Setup, run the installer, for all users). A per-user install would also leave the
  exe writable by the user, who runs it as admin.
- **A build started by a tag push, a draft release, a separate repo or the server as the update
  source:** ruled out by the owner decisions and the ADR-0070 amendment.
