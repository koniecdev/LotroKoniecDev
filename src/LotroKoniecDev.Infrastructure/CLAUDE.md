# Infrastructure Layer

Platform-specific implementations. Depends on Application (implements its abstractions) and Domain.

## Structure

```
DatFile/
  DatExportNative.cs              P/Invoke declarations for datexport.dll (8 DLL imports)
  DatFileHandler.cs               IDatFileHandler impl. Thread-safe (lock), IntPtr marshaling, IDisposable
Discovery/
  DatFileLocator.cs               IDatFileLocator impl. Multi-stage LOTRO detection:
                                    1. Standing Stone Games default path
                                    2. Steam path
                                    3. Windows Registry (3 keys)
                                    4. Full disk scan on fixed drives
                                    5. Local fallback (data/client_local_English.dat)
Diagnostics/
  GameProcessDetector.cs          IGameProcessDetector impl. Checks: lotroclient, lotroclient64, LotroLauncher, TurbineLauncher
  WriteAccessChecker.cs           IWriteAccessChecker impl. Creates temp file to verify write access
Network/
  ForumPageFetcher.cs             IForumPageFetcher impl. Scrapes LOTRO release notes forum
Storage/
  VersionFileStore.cs             IVersionFileStore impl. Reads/writes game version to text file
InfrastructureDependencyInjection.cs  AddInfrastructureServices()
```

## Known Issues

- `DatFileHandler.Open()` discards `vnumDatFile` and `vnumGameData` from `OpenDatFileEx2()` (line 39: `out _`). These contain DAT version info needed for game update confirmation. Must be exposed via `IDatVersionReader`.

## Planned Additions (M1)

- `IDatVersionReader` impl — read vnumDatFile/vnumGameData without full handler lifecycle
- `IDatFileProtector` impl — `attrib +R/-R` on DAT file (currently in lotro.bat)
- `IGameLauncher` impl — `Process.Start("TurbineLauncher.exe")` with wait

## DI Registration

```csharp
services.AddInfrastructureServices();
// Registers: IDatFileHandler(Scoped), IDatFileLocator(Singleton),
//            IGameProcessDetector(Singleton), IWriteAccessChecker(Singleton)
```

## Native Interop (datexport.dll)

Key P/Invoke methods:
- `OpenDatFileEx2(path, flags)` -> handle
- `GetNumSubfiles(handle)` -> count
- `GetSubfileSizes(handle, fileIds, sizes, iterations, versions)`
- `GetSubfileData(handle, fileId, data, offset)` -> bytes read
- `PutSubfileData(handle, fileId, data, size, version, iteration)`
- `Flush(handle)`, `CloseDatFile(handle)`

Memory managed via `Marshal.AllocHGlobal`/`Marshal.FreeHGlobal`. Handles tracked in `_openHandles` dictionary with lock for thread safety.

## DatFileLocator Discovery Order

Searches for `client_local_English.dat` at:
1. `C:\Users\{user}\AppData\Roaming\Standing Stone Games\...`
2. Steam install path
3. Registry: `HKLM\SOFTWARE\WOW6432Node\...` (3 different keys)
4. Recursive scan of all fixed drives (looks for LOTRO directories)
5. Local `data/` folder as last fallback

Results are deduplicated by normalized path.
