# E2E Test Data

The `TestData/` subfolder holds real game files needed for end-to-end tests.

## Setup

1. Locate your LOTRO installation (e.g. `C:\Program Files (x86)\Standing Stone Games\The Lord of the Rings Online\`)
2. Copy `client_local_English.dat` into `TestData/` (next to the placeholder file there)
3. Run: `dotnet test tests/LotroKoniecDev.Tests.E2E`

If the DAT file is not present, all E2E tests will be **skipped** (not failed).

## Notes

- `*.dat` files are gitignored — each developer copies their own
- The DAT file is ~4 GB, do not commit it
- Tests use a temporary copy of the DAT for patch operations (originals are not modified)
