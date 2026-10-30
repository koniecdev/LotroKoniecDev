# ADR-0069: The patcher starts the game launcher without checking it

**Status:** Accepted
**Date:** 2026-10-30
**Decision-makers:** Solo maintainer (owner decision recorded in #1290, 2026-10-30)
**Amends:** [ADR-0067](0067-who-checks-what-each-of-our-parts-checks-only-what-it-owns.md) §7 item 5
**Related:** Patcher (`Infrastructure/GameLaunching/GameLauncher.cs`, `Cli/DatPathResolver.cs`,
`Cli/LauncherNotice.cs`, `Cli/Commands/LaunchCommand.cs`); spec 0011 LEGAL-12; tickets #392
(AUDIT-SEC-02), #1290, #1292, #43; the Update 50 measurement
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
2. **The guard is how the folder was chosen.** A folder from `-d` or a known install location was
   chosen by the player or by an installer. Any other folder is used only after the player confirms
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
  gives `Users` full control, and that location is used with no question. A game on a second drive
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

## References

- #392: AUDIT-SEC-02, the attack and the two guards
- #1290: the unsigned launcher, the options and the owner's choice
- #1292: a test for the scan confirmation
- #43: the desktop app's auto-detection
- spec 0011, LEGAL-12: the risk notice the launcher notice follows
- `docs/knowledge-base/update-50/RESULTS.md` (PR #1291): the measurement that found it
