# ADR-0069: The patcher starts the game launcher without checking it

**Status:** Accepted
**Date:** 2026-10-30
**Decision-makers:** Solo maintainer (owner decision recorded in #1290, 2026-10-30)
**Amends:** [ADR-0067](0067-who-checks-what-each-of-our-parts-checks-only-what-it-owns.md) §7 item 5
**Related:** Patcher (`Infrastructure.Windows/GameLaunching/GameLauncher.cs`, `Cli/DatPathResolver.cs`,
`Cli/LauncherNotice.cs`, `Cli/Commands/LaunchCommand.cs`); spec 0011 LEGAL-12; tickets #392
(AUDIT-SEC-02), #1290, #1292, #1298, #43; the Update 50 measurement
(`docs/knowledge-base/update-50/RESULTS.md`, PR #1291)

## Context

`launch` patches the translations and then starts `LotroLauncher.exe` from the folder of the game
file (`client_local_English.dat`). That folder comes from `-d`, from a known install location (the
SSG default, Steam, the registry) or, when none of those exists, from a scan of every fixed drive.
The `data/` folder under the working directory is added last; under `lotro.bat` that is the
patcher's own checkout. `lotro.bat` runs the tool as administrator, and the launcher it starts gets
the same rights.

AUDIT-SEC-02 (#392) found the attack in that chain: someone puts a game file and a fake launcher in a
folder anyone can write to, the disk scan finds it, and the fake launcher runs as administrator. Two
guards were added in July 2026: the player must confirm a folder the scan found, and the launcher
must carry an Authenticode signature from Standing Stone Games or Turbine.

During the Update 50 measurement (2026-10-29) the second guard refused the real launcher. SSG no
longer signs it:

| File | Version | Signature |
|---|---|---|
| `LotroLauncher.exe`, 49.8 | 4908.0071.2801.4041 | none |
| `LotroLauncher.exe`, 50 | 5000.0071.3771.4007 | none |
| `LotroLauncher.exe` from the 2019 installer | 2305.0061.1867.4359 | valid, "Standing Stone Games" |
| `lotroclient.exe`, `x64\lotroclient64.exe` | 4908…, 5000… | valid, "Standing Stone Games LLC" |

The installer still ships the signed 2019 launcher, but the launcher updates itself on its first start
and keeps only the previous version in `backup\`. So `launch` patched, repaired, and then refused to
start the game, with exit code 3, for every player with a current install.

A signature also never covered the whole attack. The launcher, the old signed one included, loads
`QtCore4.dll`, `QtGui4.dll`, `QtNetwork4.dll`, `QtWebKit4.dll` and `QtXml4.dll` from the game folder,
and none of them is signed. Anyone who could put a launcher in that folder could put one of these
libraries there instead, and a signed launcher would load it with the same rights.

## Decision

1. **`launch` starts `LotroLauncher.exe` from the game folder with no check of the file.** The
   Authenticode check, its native wrapper and its `GameLaunch.UntrustedLauncher` error are removed.
   With every current launcher unsigned, the check could not tell a real launcher from a fake one,
   so keeping it anywhere would only block.
2. **The guard is how the folder was chosen.** A folder from `-d` or a known install location is
   used with no question: the player or an installer chose it. The Steam default folder is used
   with no question too, although any user on the PC can create the game folder in it; whether to
   ask about it is open in #1298. Any other folder is used only after the player confirms
   it, or picks it from the list (`DatPathResolver`, #392 criterion 2): a folder the disk scan found,
   and `data/` under the working directory when it is the only location. The tool can be started
   from any folder, so `data/` there is not always the patcher's own. This question is now the only
   guard before the launcher starts. It has no test yet; #1292 adds one. `GameLauncher` turns the
   DAT path into a full path first, so the file it finds is the file Windows starts.
3. **`launch` prints a notice on every run** (`LauncherNotice`, in Polish, next to the LEGAL-12 risk
   notice; both are also in the `NOTICE` file next to the exe). It says that the files in the game
   folder are not checked because SSG no longer signs the launcher; that the launcher gets the
   tool's rights, administrator through `lotro.bat`, and asks for admin rights itself when it cannot
   write to the game folder; and that a file a virus replaced can get those rights this way, so the
   player starts the game knowingly. It only informs: no question and no change to exit codes, as
   spec 0011 LEGAL-12(b) asks of the risk notice ("no confirmation prompt — do not annoy").
4. **ADR-0067 §7 item 5 gets one exception.** The launcher and the libraries in the game folder are
   input from outside our trust boundary, but they get no check of who made them. A review finding
   that asks for a signature, hash or publisher check of the launcher or of those libraries is
   already decided: point here. The checks on the game file's content stay (the source guard of
   ADR-0047, the Tier 0 snapshot). How the folder is chosen stays a finding: the scan confirmation,
   a new location source, or a path that reaches the launcher without one of them.

## Consequences

### Positive

- `launch` starts the game again with every current install.
- About 280 lines of code for the native signature check (`WinVerifyTrust`), and its tests, are
  gone.

### Negative / Accepted Trade-offs

- **Whoever can write the game folder can run code with the tool's rights.** That includes malware
  running as the player, which can also change the source tree and the NuGet cache that `lotro.bat`
  builds as administrator. It also includes a second, non-admin account on the same PC when the
  game sits in a folder all users can write to: the Steam default `C:\Program Files (x86)\Steam`
  gives `Users` full control, and that location is used with no question. Such an account can also
  create a game folder there for a player whose game is elsewhere (#1298). A game on a second drive
  may be the same. In the SSG default folder under Program Files, only an administrator can write.
  The notice tells the player this; nothing in the code prevents it.
- The only automated test of the AUDIT-SEC-02 attack went with the check. The confirmation that
  stays had no test before this change either (#1292).
- The desktop app (M4) calls the locator directly, so it must ask the same question for a scanned
  folder and show the same notice (tasks added to #43).

## Alternatives Considered

### A. Keep the signature check, but only for a folder the disk scan found

No current SSG launcher has a signature, so this refuses every scanned install, real or fake. It is a
block, not a check. The ticket first recommended it; it was withdrawn on 2026-10-30 for this reason.

### B. Accept an unsigned launcher only in a folder that a normal user cannot write to

This still checks something, but it refuses every install in a user-writable folder: the Steam
default, `D:\Games` and others, which many players choose to avoid the UAC prompt. The owner rejected
it.

### C. Pin the hashes of known launchers

SSG replaces the launcher with every big update (Update 50 did), so the list would break the launch
after each one.

### D. Check the company name in the file's version information

Anyone can write that text into a file.

### E. Start the launcher without administrator rights, even under `lotro.bat`

This would not remove the risk. The real launcher asks for admin rights itself whenever it cannot
write to the game folder, which is every start in Program Files, so players accept that UAC prompt
by habit. A planted launcher could ask the same way, and with the real one unsigned too, both
prompts look the same. It also needs extra Windows plumbing (a token taken from Explorer, or a
scheduled task). Not chosen.

**Reopen this decision** if SSG signs the launcher and its libraries again, or if a player reports a
planted launcher or library.

## Implementation Notes

- Removed: `Application/Abstractions/ILauncherSignatureVerifier.cs`,
  `Infrastructure/GameLaunching/AuthenticodeLauncherSignatureVerifier.cs`,
  `Infrastructure/GameLaunching/WinTrustNative.cs`, `DomainErrors.GameLaunch.UntrustedLauncher`, the
  DI line, `AuthenticodeLauncherSignatureVerifierTests` and the signature test in `GameLauncherTests`.
  `GameLauncher` also drops the read handle it held from the check to the start, because it only
  protected that window.
- Added: `Cli/LauncherNotice.cs`, printed by `LaunchCommand` after `RiskNotice`, and the same text in
  `NOTICE`. `DatPathResolver` asks about `data/` too when it is the only location, and its question
  and the warning in the list of installs say that nothing checks the launcher. `GameLauncher`
  resolves the full path before it checks and starts the file.
- Tests: `GameLauncherTests` checks that a launcher Windows cannot run ends as `GameLaunch.Failed`
  and that a relative DAT path is resolved to a full path; `ErrorPathE2ETests` checks that `launch`
  prints the notice.
- ADR-0067 §7 item 5, the `code-reviewer` agent's list of what stays a finding, and the "Sort review
  findings" row in `CLAUDE.md` point here.

## Amendment (2026-11-17) — the desktop app (#43)

**Ticket:** #43 (M4-03). Owner rulings: #1292 (2026-11-16, an allow-list) and #43 (2026-11-16).

1. **The rule lives in Domain.** `DatFileLocation.NeedsConfirmation` is true unless the folder came
   from the SSG default, Steam or the registry, so a source added later asks by default (#1292). The
   app uses it now. The CLI's `DatPathResolver` moves to it in #1292.
2. **How the app asks.** On a first run with no usable saved folder, the app searches in the
   background:
   - One known install is saved with no question.
   - One folder from any other source gets a Tak/Nie question that says nothing checks the launcher.
   - Several installs are shown as a list, with that warning on every entry that needs
     confirmation. A click on an entry is the confirmation.
   - Nothing found, or „Nie”, leads to the folder dialog.

   A folder the player picks in the dialog or types in the settings counts like `-d`: the player
   chose it. A search result is ignored once the player opens the folder dialog or the settings
   window, so a late result never chooses for a player who is already choosing.
3. **The saved folder is a new location source (§4).** The app keeps the game folder in
   `%AppData%\LotroPoPolsku\config.json` and uses it on later starts with no question: the player
   chose it or confirmed it, or it came from the allow-list. A saved folder that lost its game file
   is replaced only after the player says so: a Tak/Nie that names both folders, a pick from the
   list, the folder dialog or a save in the settings window. Until then the file keeps the old
   folder. A single bad value in the file drops only that value. When the app could not read the
   file, it is not a JSON object, or a value the app reads holds an escape that is not text, the app
   starts from the defaults. Its first save then moves the old file aside, and a file that could
   not be read may only be locked for now, so the app asks before it saves a known install found
   then, even one from the allow-list. A translation file the settings name that is gone at start
   stays in the settings, because its drive may only be unplugged. Since #1464 the file is
   `{app}\data\config.json` (amendment of 2026-11-18).
4. **The risk, and who closes it.** Any program that runs as the player can change that file, and
   so point the app at a folder of its choice. Today nothing reaches the launcher or the DAT with it:
   Patch and Play stay disabled until #42. The app also writes the file as administrator in some
   runs: right after each install or update (#45's installer starts it with `runascurrentuser`),
   and on every start once #42 adds `requireAdministrator`. The player can write to the folder the
   file sits in, so this is the same class as the installer's log on #42's list of same-user paths.
   A file the load could not use is moved aside before the next save, to a new random name next to
   it, never over an existing file; the temporary file of a save has a random name too. So nothing
   can be placed in advance at a name the settings code writes, apart from `config.json` itself. The
   folder itself is the player's too: a program that runs as the player can swap it for a junction
   that points somewhere else. #1456 reviews that with the rest of this item.
   The app's log, `LotroPoPolsku.log` (#41), is one more fixed name in that folder, open until #42.
   **Before Patch or Play uses the saved folder, #42 closes this read path (for example, settings
   that an elevated app reads from a folder only administrators can write), or the owner accepts it
   in an amendment to this ADR.** Closed by #1464: see the amendment of 2026-11-18.
5. **The notice.** The main window always shows the LEGAL-12 risk note and the app's own launcher
   notice, in the CLI's order. From #1464 on, the app runs as administrator (owner decision 4
   on #43), so the launcher it starts already has those rights. Its text therefore leaves out the
   CLI's clause that the launcher asks for admin rights itself. `NOTICE` now describes both
   programs. So §3's "both
   are also in the `NOTICE` file" and the Implementation Notes' "the same text in `NOTICE`" no
   longer hold word for word: `NOTICE` carries what both texts say, not either text exactly.
6. **Proton.** Whether the search finds a Steam install under Proton is unproven. The Steam path is
   read inside the Wine prefix, and the drive scan may walk the Linux file tree. Nothing here
   promises the folder dialog as the outcome.

## Amendment (2026-11-18) — the app keeps its files where only administrators can write (#1464)

**Ticket:** #1464 (M4-13). Owner rulings: #42 (2026-11-16) point 2 (admin rights once, at start,
never a restart into an elevated process) and point 3 (the log file is the session's choice); #45
(2026-11-16) point 4 (uninstall removes only what Setup installed). This amendment closes item 4 of
the amendment of 2026-11-17.

1. **Admin rights at start.** The manifest asks for `requireAdministrator`, in #1464 instead of
   #42, because an elevated app is safe only with the folders below, and these folders are safe
   only for an elevated app. #42 wires Patch and Play on top.
2. **One folder for everything.** Everything the app reads or writes for itself sits under the
   exe's folder (`AppContext.BaseDirectory`):
   - `data\`: `config.json` and `LotroPoPolsku.log`;
   - `updates\`: one folder per downloaded installer, and the installer's `update-install.log`;
   - from #42 on, the downloaded `polish.txt` with its sidecars (`.etag`, `.endpoint`, `.ledger`,
     `.iterations`) and `last_known_game_version.txt`, under `data\`. The rule: no elevated write
     next to a path the player chose. When the settings name a translation file of the player's,
     the app may read it, but its sidecars stay in `data\`; #42 names those files (ADR-0047 §4).

   Installed, that folder is `{autopf}\LotroPoPolsku`, under Program Files (x86). Windows' default
   rights there let only administrators create, change or delete anything, and a folder the
   elevated app creates inherits them, so the app needs no access-list code of its own. A developer
   build uses its own `bin` folder by the same rule. The player can write there, as Consequences
   already accepts for the source tree that `lotro.bat` builds as administrator.
3. **The installer has no folder page** (`DisableDirPage=yes`). A folder outside Program Files,
   such as `C:\Games`, inherits write rights for every user from the drive root, which would open
   every path again and make the exe itself replaceable. `/DIR=` and an earlier install's folder
   can still move it, and both need an administrator. A per-user install
   (`PrivilegesRequiredOverridesAllowed`) is never added.
4. **Code loads only from that folder.** The native libraries (`datexport.dll` and its C runtime,
   Skia, HarfBuzz, ANGLE) ship loose next to the exe. The exe no longer unpacks them into the
   player's `%TEMP%` at start. Startup hooks are off in Release, so `DOTNET_STARTUP_HOOKS` in the
   player's own environment loads nothing.
5. **One running copy.** A second start shows a Polish message and ends; the first copy keeps
   running. The `Global\LotroPoPolsku_Running` mutex decides, so a copy in another Windows account
   counts: every account shares one install, one `data\` and one game. A program that holds that
   name can stop the app from starting. That is a denial of service, not a way to admin rights,
   and is accepted.
6. **What stays open**, for the review in #1456:
   - The .NET runtime reads variables from the player's own environment (`HKCU\Environment`), which
     the player changes without admin rights. The CLR profiler variables
     (`CORECLR_ENABLE_PROFILING`, `CORECLR_PROFILER_PATH`) load a DLL into the elevated app, and the
     EventPipe and crash dump variables make it write to a path the player picks. No project
     setting turns them off. UAC on the same desktop is not a security boundary, so this amendment
     closes the file paths of item 4, not every way from a player's program to admin rights.
   - A PC whose policy makes the creator, not Administrators, the owner of what an administrator
     creates, or that runs Windows Administrator Protection. There `data\` may belong to the player.
     Not proven either way, and no code of ours covers it.
   - The game folder. On Steam and in custom folders the player can already write it
     (Consequences). The rule above protects the app's own files; it does not make such a game
     folder safe.
7. **What players see.** The settings and the log are per PC now, not per Windows account: several
   admin accounts on one PC share one game folder and one set of settings, and every account can
   read them. Editing `config.json` by hand needs an editor started as administrator. The
   uninstaller leaves `data\` and `updates\` in the install folder (#45 point 4). `data\` is not a
   place for a DAT: the locator's `data\client_local_English.dat` probe resolves against the working
   directory, which is the install folder for both shortcuts. Only an administrator can put a file
   there, and that source still needs the player's confirmation (amendment of 2026-11-17, item 1).

**Alternatives weighed** (#1464's triage and its two challenges):

- A helper process for Patch and Play only, with the app not elevated: the #42 ruling asks for
  `requireAdministrator`, and the helper would still act on what the unelevated app tells it.
- `%ProgramData%` with an access list set by the installer: Users may create folders there, so a
  program can create the folder first and own it.
- Settings in HKLM: the log, the downloads and the libraries still need a protected folder.
- The folder page plus an access list on the chosen folder: the parent folder stays the player's,
  who can rename the install folder away and put another one in its place.
- A check at start that refuses a folder the player can write: a replaced exe never runs the check.

## References

- #392: AUDIT-SEC-02, the attack and the two guards
- #1290: the unsigned launcher, the options and the owner's choice
- #1292: a test for the scan confirmation
- #1298: a game folder another account creates in the Steam folder
- #43: the desktop app's auto-detection
- spec 0011, LEGAL-12: the risk notice the launcher notice follows
- `docs/knowledge-base/update-50/RESULTS.md` (PR #1291): the measurement that found it
