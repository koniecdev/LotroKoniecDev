# E2E Test Data

The `TestData/` subfolder holds real game files needed for end-to-end tests.

## Setup

1. Locate your LOTRO installation (e.g. `C:\Program Files (x86)\Standing Stone Games\The Lord of the Rings Online\`)
2. Copy `client_local_English.dat` into `TestData/` (next to the placeholder file there)
3. Run: `dotnet test tests/LotroKoniecDev.Tests.E2E`

If the DAT file is not present, all E2E tests will be **skipped** (not failed).
The suite runs only on Windows: elsewhere `dotnet test` prints one "not run" line instead (#816).

## Notes

- `*.dat` files are gitignored — each developer copies their own
- The DAT file is ~2 GB, do not commit it
- Tests never change the original DAT. The tests of one class share one temporary copy in
  `%TEMP%\lotro_e2e_*`, deleted when the class's last test is done (#1408): `PatchedDatCopy` is
  patched once before any test checks it, and every patch test checks that one result (three tests
  patch it again afterwards); `SharedDatCopy` is left as each test found it. A run normally holds
  one copy and its backup at a time (a copy whose delete fails stays until the run ends), so keep
  about 5 GB free in `%TEMP%`. A test that truly needs a fresh copy calls `CreateTempDatCopy()`
  itself, and says why
- Close LOTRO and its launcher first: `patch` refuses to run while either is open, even on a copy
