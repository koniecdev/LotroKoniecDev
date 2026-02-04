# LOTRO Polish Patcher - Plan

## Co mamy teraz

- CLI do exportu i patchowania DAT — dziala
- Tlumaczenia w pliku tekstowym (pipe-delimited) — edycja uciazliwa
- Kod CLI trudny w utrzymaniu — brak porzadnej architektury
- Single-user — tylko ja tlumacze

## Cel

Lokalna aplikacja webowa do tlumaczen. Odpalam, otwieram przegladarke, tlumacze.
Zadnego auth, zadnych uslug zewnetrznych. MSSQL lokalnie (Docker lub LocalDB).

## Jak to ma dzialac

```
1. CLI export     DAT -> exported.txt (angielskie teksty)
2. Web import     exported.txt -> baza danych
3. Web            tlumacze side-by-side EN/PL
4. Web export     baza -> polish.txt
5. CLI patch      polish.txt -> DAT
6. Gram
```

## Architektura

```
┌─────────────────────────────────┐
│  Blazor SSR (localhost:5000)   │
└───────────────┬─────────────────┘
                │ MediatR
┌───────────────▼─────────────────┐
│  Application (handlery)         │
├─────────────────────────────────┤
│  Domain (modele, Result, Errors)│
├────────────────┬────────────────┤
│ Persistence    │ DatFile        │
│ EF Core, MSSQL │ P/Invoke       │
└────────────────┴────────────────┘

CLI uzywa tych samych handlerów co Web App.
```

## Projekty

| Projekt | Opis |
|---------|------|
| Primitives | Stale, enumy |
| Domain | Modele, Result, Errors |
| Application | MediatR handlery |
| Infrastructure.Persistence | EF Core, MSSQL |
| Infrastructure.DatFile | P/Invoke do DAT (tylko CLI) |
| CLI | Konsola Windows |
| WebApp | Blazor SSR |

## Baza danych

MSSQL przez Docker lub LocalDB. Trzy tabele:

```sql
Languages (Code, Name, IsActive)
ExportedTexts (Id, FileId, GossipId, EnglishContent, ImportedAt)
Translations (Id, FileId, GossipId, LanguageCode, Content, ArgsOrder, ArgsId, IsApproved, Notes, CreatedAt, UpdatedAt)
```

Multi-language w strukturze (LanguageCode), ale na razie tylko Polish.

---

## M1: Porzadki w CLI (MediatR)

Refaktor CLI na MediatR. Kod staje sie utrzymywalny, handlery beda wspoldzielone z web app.

| # | Co zrobic |
|---|-----------|
| 1 | Rozdzielic TFM — per-project zamiast globalnego net10.0-windows/x86 |
| 2 | Dodac MediatR |
| 3 | Zaprojektowac IProgress<T> dla handlerow |
| 4 | ExportTextsQuery + Handler (zastepuje Exporter) |
| 5 | ApplyPatchCommand + Handler (zastepuje Patcher) |
| 6 | PreflightCheckQuery + Handler |
| 7 | Refaktor CLI Program.cs na IMediator.Send() |
| 8 | Usunac stare serwisy (IExporter, IPatcher, statyczne komendy) |
| 9 | Zaktualizowac DI |
| 10 | Ogarnac ArgsOrder/ArgsId (podlaczyc albo usunac) |
| 11 | Ogarnac pole approved w uzyciu |
| 12 | Testy jednostkowe dla handlerow |
| 13 | Testy integracyjne |

**Po M1:** CLI dziala jak wczesniej, ale kod jest czysty.

---

## M2: Baza danych

MSSQL + EF Core. Import/export przez handlery.

| # | Co zrobic |
|---|-----------|
| 14 | Dodac EF Core + SQL Server NuGet |
| 15 | Zaprojektowac entities (TranslationEntity vs Domain.Translation) |
| 16 | AppDbContext + konfiguracja |
| 17 | ITranslationRepository |
| 18 | IExportedTextRepository |
| 19 | Implementacja repozytoriow |
| 20 | Migracje EF + auto-migrate w ustawieniach dev |
| 21 | ImportExportedTextsCommand + Handler |
| 22 | Translation CRUD (Commands/Queries) |
| 23 | ExportTranslationsQuery + Handler (DB -> polish.txt) |
| 24 | Migracja istniejacego polish.txt do bazy |
| 25 | Parser exported.txt |
| 26 | Obsluga separatora \|\| w tresci |
| 27 | Seed jezyka polskiego |
| 28 | Testy |

**Po M2:** Baza dziala, moge importowac/exportowac przez handlery.

---

## M3: Aplikacja webowa

Blazor SSR. Lista, edytor, import, export.

| # | Co zrobic |
|---|-----------|
| 29 | Stworzyc projekt Blazor SSR |
| 30 | Layout i nawigacja (Bootstrap) |
| 31 | DI: MediatR, EF Core, DbContext |
| 32 | Lista tlumaczen (tabela, szukaj, filtruj, sortuj, paginacja) |
| 33 | Edytor tlumaczen (side-by-side EN/PL) |
| 34 | Podswietlanie <--DO_NOT_TOUCH!--> i walidacja \|\| |
| 35 | Przegladarka plikow (grupuj po FileId, szukaj) |
| 36 | Dashboard (postep, ostatnie edycje) |
| 37 | Import (upload exported.txt) |
| 38 | Export (pobierz polish.txt) |
| 39 | Obsluga bledow (Result -> komunikaty) |
| 40 | Docker (docker-compose: Web App + MSSQL) |
| 41 | Auto-migrate + seed przy starcie |
| 42 | Testy |

**Po M3:** Tlumacze w przegladarce.

---

## Podsumowanie

```
M1: #1-#13   Porzadki w CLI
M2: #14-#28  Baza danych
M3: #29-#42  Aplikacja webowa
```

**42 issues.** Po M3 moge tlumczyc.

---

## Pozniej (gdy bedzie potrzeba)

- Auth (OpenIddict) — gdy pojawi sie community
- Web API — gdy potrzebny remote access
- Multi-user — SubmittedById, ApprovedById, review workflow
- Glossary — slownik terminow
- TranslationHistory — historia edycji
- AI review — LLM sprawdza bledy
- Powiadomienia — Discord, email
