# datexport.dll: the read-only open flag is bit `0x4` (2026-08-07)

**Status:** settled — measured two independent ways, then proven end-to-end against the live
Program Files DAT. Supersedes the "`export` always needs elevation" claim in
[live-test-2026-06-25.md](live-test-2026-06-25.md), [update-48.7/BASELINE.md](update-48.7/BASELINE.md)
and [update-49/BASELINE.md](update-49/BASELINE.md).

## The finding

`OpenDatFileEx2`'s `flags` parameter selects the access the native library asks the OS for **on bit
`0x4` only**. Our `DatExportNative.OpenFlagsRead` was `2`, which never set that bit — so every
"read-only" open still requested `GENERIC_READ | GENERIC_WRITE` and failed on any file the caller
cannot write. `OpenFlagsRead = 6` (`0x2 | 0x4`) is the fix, and it is the whole fix.

The `Read (2) + Write (128) = 130` comment that shipped with the P/Invoke wrapper is what misled
everyone, including the measurement that concluded the feature was impossible: `2` is present in
**both** constants and selects nothing about access.

## Evidence 1 — disassembly (static, no elevation needed)

`datexport.dll` is x86, imagebase `0x10000000`, single `CreateFileA` import at IAT `0x1002d0e0`.
The open helper at `0x10012bb0` (capstone + pefile):

```asm
mov  eax,[esp+0xc]      ; mode flags
test al,8    -> ebx=2   ; CREATE_ALWAYS
test al,0x10 -> ebx=4   ; OPEN_ALWAYS       (default ebx=3 = OPEN_EXISTING)
and  eax,4              ; <-- the only bit that touches dwDesiredAccess
mov  edi,0xC0000000     ; GENERIC_READ | GENERIC_WRITE
je   0x10012bdf         ;   taken when (flags & 4) == 0
mov  edi,0x80000000     ; GENERIC_READ only …
xor  esi,esi            ; … and dwShareMode becomes FILE_SHARE_READ
...
push edi                ; dwDesiredAccess -> CreateFileA
```

`OpenDatFileEx2` itself (export RVA `0x4d90`) branches on the same bit before handing the flags
down — `shr ecx,2 / not cl / test cl,1` → `or eax,0x1800` only when `0x4` is clear.

So the library has always had a genuine read-only path. We never asked for it.

## Evidence 2 — flag matrix (dynamic)

32-bit PowerShell P/Invoke straight into the DLL (the CLI is `win-x86`, so a 64-bit host cannot load
it), one 1.77 GB DAT, three ACL/attribute conditions:

| flags | writable file | ACL `Users:(RX)` | `attrib +R` |
|---|---|---|---|
| `2` — the old `OpenFlagsRead` | OK | **FAIL** | **FAIL** |
| `130` — `OpenFlagsReadWrite` | OK | **FAIL** | **FAIL** |
| **`4`** | OK | **OK** | **OK** |
| **`6`** (`0x2\|0x4`) | OK | **OK** | **OK** |
| **`134`** (`0x2\|0x4\|0x80`) | OK | **OK** | **OK** |
| `0` | OK | **FAIL** | **FAIL** |

`GetNumSubfiles` returns the same 303,792 on every successful open, so `0x4` does not degrade the
handle — it only changes what the OS is asked for.

## Evidence 3 — end-to-end, live Program Files, non-elevated

`OpenFlagsRead = 6`, `export` run from a **non-elevated** shell against
`C:\Program Files (x86)\StandingStoneGames\The Lord of the Rings Online\client_local_English.dat`
(`Users:(RX)`, write denied — confirmed before the run):

- exit **0**, 281,253 text files / 800,864 fragments
- live DAT SHA-256 `D2D25235…B721` **byte-identical** before and after
- same DAT content exported from a writable copy → output **byte-identical** to the read-only run
- the **directory** is not writable either (`TrustedInstaller`/`SYSTEM`/`Administrators` full, no
  write for us; creating a file in it is denied), and the export still succeeded — so the "write
  **or sidecar** intent" half of the 2026-06-25 theory is disproven too: the read path creates
  nothing next to the DAT

Suites after the change: Unit 3991/3991, Architecture 21/21, Infrastructure 42/42, **E2E 26/26** —
including `ExportE2ETests.Export_ShouldExitWithZero_WhenDatCopyIsReadOnly`, the #446 feasibility gate
that had been red on `main` since 2026-07-11, now passing **with its assertion untouched**.

> **E2E flakiness worth knowing:** the suite makes one full 1.77 GB DAT copy per DAT-touching test
> (14 today) and keeps them all until the fixture disposes, while `RunCliAsync` enforces a hard 120 s
> per-process timeout. `Patch_ShouldFail_WhenTranslationFileHasOnlyGarbage` already runs ~76 s, so
> under I/O pressure it tips over the limit and fails for reasons that have nothing to do with the
> code — seen once here, with 7.1 GB of orphaned `%TEMP%\lotro_e2e_*` dirs left by a killed run
> (24 min wall clock; the clean re-run was 14.6 min, 26/26). **Sweep `%TEMP%\lotro_e2e_*` before
> trusting a red E2E run.**

## The measurement trap that produced the wrong answer first

The morning's investigation built its "faithful Program Files proxy" with
`icacls <file> /deny "<user>:(W)"`. That is not a read-only file — `(W)` is `FILE_GENERIC_WRITE`,
which **includes `SYNCHRONIZE`**, and without `SYNCHRONIZE` no `CreateFile` succeeds at all. The
proxy was unreadable, every flag failed, and the conclusion generalised to "the library cannot open
read-only".

Build the proxy by **granting**, never by denying:

```powershell
icacls <file> /inheritance:r /grant:r "$env:USERDOMAIN\$env:USERNAME:(RX)"
```

…and assert both halves of the precondition before trusting the run: reads must succeed, writes must
fail. `ExportE2ETests.Export_ShouldExitWithZero_WhenDatCopyGrantsReadButNotWrite` encodes exactly
that, preconditions included.

Note the two Windows refusals are **not** interchangeable. The read-only *attribute* (`attrib +R`)
and an ACL without write are different mechanisms; the live DAT carries the ACL and no attribute.
Both are pinned, separately.

## Consequences

- `export` and `ReadVersion` need **no elevation**. Only `patch` (and therefore `launch`'s PATCH
  branch) does. `export.bat` correctly does not self-elevate; `patch.bat` / `lotro.bat` still do.
- The standing "export from a backup copy" workaround in the update baselines is obsolete — export
  straight from the live install.
- #443 Option A is delivered, which restores its "small correctness fix worth doing regardless of
  Option B" framing. It changes nothing about Option B (the VM runner): a fully-owned automation box
  runs its export task pre-elevated anyway, which is why ADR-0030 deferred it on other grounds.

## Method note

The disassembly took about ten minutes with `pip install capstone pefile` and answered a question
that had been standing since June. Reach for it before Process Monitor when the question is "can
this native call ever do X" — ProcMon shows the one call you observed, the branch shows every call
the library can make.

## Cross-reference

- [live-test-2026-06-25.md](live-test-2026-06-25.md) — where the write-intent claim was first recorded
- [live-test-2026-07-11.md](live-test-2026-07-11.md) — the session that shipped Option A untested
- [ADR-0030](../adr/0030-game-version-export-stays-manual-vm-runner-deferred.md) — the VM-runner
  deferral, amended with this outcome
- The Windows-only E2E blind spot bit for the second time here (the first was #197's path refactor,
  E2E 0/23). `Tests.E2E` is Windows-only and off CI by design, so **after any patcher change to the
  native interop, paths, CLI wiring or the .bat wrappers, run `dotnet test tests/LotroKoniecDev.Tests.E2E`
  on Windows** — nothing else will tell you.
