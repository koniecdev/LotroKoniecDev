# Dokumentacja domeny — LotroKoniecDev TMS

> Przegląd modelu domenowego **Translation Management System** (TMS). **Kod jest źródłem prawdy**:
> gdy ten dokument mówi coś innego niż kod, popraw dokument. Reguły biznesowe ustala wiki
> (`CLAUDE.md` → „Source of truth").
>
> Zakres: tylko kontekst **TMS** (`src/TranslationSystem/**`). Patcher (`src/Patcher/**`) ma własny,
> prostszy model i nie jest tu opisany.

## Spis treści

1. [Przegląd architektury](#przegląd-architektury)
2. [Agregaty](#agregaty)
   - [1. Translation (rdzeń)](#1-translation--rdzeń-domeny)
   - [2. GameVersion](#2-gameversion)
   - [3. Translator](#3-translator-adr-0004)
3. [Maszyna stanów Translation — cykl aktualizacji](#maszyna-stanów-translation--cykl-aktualizacji-spec-0001)
4. [Obiekty wartości (Value Objects)](#obiekty-wartości-value-objects)
5. [Serwisy domenowe](#serwisy-domenowe)
6. [Projekcja: PrecomputedTranslationFile](#projekcja-precomputedtranslationfile)
7. [Repozytoria i Unit of Work](#repozytoria-i-unit-of-work)
8. [CQRS — rozdział read/write](#cqrs--rozdział-readwrite-adr-0002)
9. [Obsługa błędów](#obsługa-błędów)
10. [Struktura projektu](#struktura-projektu)
11. [Wzorce projektowe](#wzorce-projektowe)

---

## Przegląd architektury

TMS to jeden z **dwóch bounded contextów** w repo. Drugi to patcher CLI. Konteksty nie mają
wzajemnych referencji projektowych. Łączy je tylko kontrakt pliku `||` (format tłumaczeń). W całym
repo **nie ma mediatora (ADR-0001)**.

Kluczowe cechy:

- **Vertical Slice Architecture.** Jeden use case to jeden plik w `…API/Features/<Obszar>/<Akcja>.cs`
  (rekord komendy lub zapytania, handler, walidator). Handlery implementują własne
  `ICommandHandler<,>`/`IQueryHandler<,>` z `SharedKernel.Messaging`. Konsument wstrzykuje
  **zamknięty** interfejs handlera, zarejestrowany jawnie w DI (ADR-0001).
- **CQRS od pierwszego dnia (ADR-0002).** Komendy ładują i zmieniają agregaty przez repozytoria
  i `IUnitOfWork`. Zapytania czytają płaskie read modele przez `IApplicationReadDbContext`. Model
  zapisu nie obsługuje list ani wyszukiwania.
- **Błędy są wartościami.** Reguła biznesowa zwraca `Result.Failure(Error)` z fabryk
  `DomainErrors.*`. Guardy (`Ensure`, `ArgumentNullException`) są tylko na błędy programisty.
- **Brak prymitywnej obsesji.** Koncept z ograniczeniem lub tożsamością to `ValueObject` albo
  strongly-typed ID, nigdy surowy `string`/`int`/`Guid`.
- **Brak domain eventów** (ADR-0002 §5): rdzeniowa pętla ich nie potrzebuje.

Kierunek zależności (warstwy TMS):

```
API  ─▶  Domain  ─▶  Primitives        (Persistence / ReadModels(+EF) / Projections / Contracts wpinają się obok)
        (ReadModels ─▶ Primitives — read modele nie znają Domain; ADR-0007 „read projections are not aggregates")
```

Trzy agregaty (`Translation`, `GameVersion`, `Translator`) i jedna **projekcja**
(`PrecomputedTranslationFile`): gotowy do pobrania plik `polish.txt`, który nie jest agregatem
(ADR-0007).

---

## Agregaty

### 1. Translation — rdzeń domeny

`src/TranslationSystem/…Domain/Aggregates/TranslationAggregate/Entities/Translation.cs`

Jeden **zmienny wiersz na fragment tekstu**, nigdy kopia na każdą wersję gry. Niesie angielskie
źródło, opcjonalny polski przekład i wskaźniki wersji. Dzięki nim widać, co zmieniło się w której
wersji, bez kopiowania wierszy przy każdym patchu (spec 0001).

| Właściwość | Typ | Rola |
|---|---|---|
| `FragmentKey` | `FragmentKey` (VO) | stała tożsamość `(FileId, GossipId)` we wszystkich wersjach gry; niezmienna |
| `Source` | `TranslationSource` (VO) | angielskie źródło: tekst i dwie kolumny argumentów jako jedna wartość |
| `TranslatedText` | `string?` | polski przekład (`null`, dopóki go nie ma) |
| `PreviousSourceText` | `string?` | zamrożone angielskie źródło, do porównania po unieważnieniu |
| `SubmittedById` | `TranslatorId?` | kto ostatnio wpisał polski (FK do `Translators`, **nie** `IdentityId` — ADR-0004) |
| `ApprovedById` | `TranslatorId?` | kto zatwierdził (FK do `Translators`) |
| `Status` | `TranslationStatus` | `Untranslated · Draft · Approved · NeedsReview` |
| `IntroducedInVersion` | `GameVersionId` | wersja, w której fragment się pojawił |
| `LastSourceChangeInVersion` | `GameVersionId?` | wersja ostatniej zmiany źródła |
| `RemovedInVersion` | `GameVersionId?` | wersja miękkiego usunięcia (`null` = aktywny) |
| `CreatedAt` / `UpdatedAt` | `DateTimeOffset` | znaczniki czasu |
| `IsRemoved` | `bool` (wyliczane) | `RemovedInVersion is not null` |

**Zachowania** (każde stempluje `UpdatedAt`):

- `CreateUntranslated(...)` — fabryka wiersza *dodanego* (pierwszy import lub diff): samo angielskie
  źródło, status `Untranslated`, stempel `IntroducedInVersion`.
- `ApplySourceChange(newSource, changedInVersion, now)` — źródło się zmieniło. Nadpisuje angielskie
  źródło. Jeśli wiersz był `Draft` lub `Approved`, **unieważnia** polski (→ `NeedsReview`) i zamraża
  stary angielski w `PreviousSourceText`. Czyści miękkie usunięcie. Stempluje
  `LastSourceChangeInVersion`.
- `MarkRemoved(removedInVersion, now)` — miękkie usunięcie: wiersz wypada z pracy i z pobieranego
  pliku, ale nigdy nie jest kasowany.
- `Restore(now)` — para wraca z identycznym źródłem: czyści usunięcie, poprzedni status (także
  `Approved`) zostaje, bo stary polski nadal pasuje.
- `ProvideTranslation(translatedText, submittedBy, now)` — zapisuje lub zastępuje polski draft
  i stempluje tłumacza. Każdy poprzedni status → `Draft`. Edycja wiersza `Approved` wyjmuje go
  z pobieranego pliku do ponownej akceptacji. Tekst dłuższy, niż zmieści DAT, rzuca wyjątek, bo
  to błąd programisty; komunikat dla tłumacza daje `UpsertTranslation.Validator` (ADR-0043).
- `Approve(approvedBy, now)` — zatwierdza do dystrybucji. Wymaga polskiej treści i nieusuniętego
  wiersza. Ustawia `Approved`, stempluje recenzenta, czyści `PreviousSourceText`, zwraca `Result`.

### 2. GameVersion

`src/TranslationSystem/…Domain/Aggregates/GameVersionAggregate/Entities/GameVersion.cs`

Wersja gry rejestrowana ręcznie przez admina (watcher forum M2-18/#85 przesunięty po MVP, ADR-0030).
Identyfikator wersji to `LotroNotationVersion` (VO w postaci kanonicznej). Status:
`Unprocessed → Processed`, z boczną ścieżką `Superseded`.

| Właściwość | Typ | Rola |
|---|---|---|
| `LotroNotationVersion` | `LotroNotationVersion` (VO) | kanoniczna notacja kropkowa, np. `48.0` |
| `DetectedAt` | `DateTimeOffset` | czas rejestracji = porządek wersji (**bez porównywania numerów**) |
| `Status` | `GameVersionStatus` | `Unprocessed · Processed · Superseded` |

**Zachowania** (zwracają `Result` i bronią przejść stanu):

- `MarkAsProcessed()` — `Superseded` nie może zostać przetworzony. Każdy inny status → `Processed`.
  Ponowny import do wersji już przetworzonej jest dozwolony i nic nie zmienia.
- `MarkSuperseded()` — `Processed` nie może przejść w `Superseded` (zrobionej pracy nie cofamy).
  Starsze nieprzetworzone wersje są oznaczane hurtem, gdy nowsza zostaje przetworzona.
- `EnsureCanBeDeleted()` — usunąć nie można tylko wersji `Processed`, bo tylko do niej był import
  i wskazują na nią tłumaczenia. `Unprocessed` i `Superseded` można usunąć, co zwalnia ich numer
  (#624). Warunek „żadne tłumaczenie nie wskazuje na wersję" sprawdza handler `DeleteGameVersion`.
- `Create(version, detectedAt)` — fabryka; nowa wersja jest `Unprocessed`.

### 3. Translator (ADR-0004)

`src/TranslationSystem/…Domain/Aggregates/TranslatorAggregate/Entities/Translator.cs`

Lokalny dla TMS, chudy profil zalogowanego użytkownika, który edytuje tłumaczenia. Trzyma nazwę
pokazywaną w edytorze (`DisplayName`, opcjonalny `Email`). Kluczem jest `IdentityId`, czyli id
użytkownika z AuthSystem. Profil powstaje **leniwie i idempotentnie** przy **pierwszym
uwierzytelnionym żądaniu** (`TranslatorProvisioningMiddleware`; ADR-0004, aneks 2026-06-24).
Handlery zapisu same też tworzą profil przed stemplowaniem FK. Wynik „tożsamość → `TranslatorId`"
jest w cache (`HybridCache`, TTL 5 min, z odciskiem claimów — PERF-07). Szczegóły:
[auth-tutorial.md](auth-tutorial.md) i ADR-0004.

| Właściwość | Typ | Rola |
|---|---|---|
| `IdentityId` | `IdentityId` (STID) | jedyna referencja do Auth między kontekstami; **unikalna** |
| `DisplayName` | `DisplayName` (VO) | nazwa w edytorze (z claimu `name`) |
| `Email` | `Email?` (VO) | opcjonalny (claimu `email` może nie być) |
| `ProvisionedAt` | `DateTimeOffset` | czas utworzenia profilu (niezmienny) |

**Zachowania:**

- `RefreshProfile(displayName, email)` — kopiuje `DisplayName`/`Email` z bieżących claimów, gdy się
  zmieniły. Przemianowane konto nadąża samo, bez osobnej synchronizacji. Nie stempluje czasu.
- `Erase()` — po usunięciu konta w AuthSystem (ADR-0065) zdejmuje osobę z profilu: `DisplayName`
  staje się `Usunięte konto` (`Translator.ErasedDisplayName`), `Email` = `null`. Profil i jego
  tłumaczenia zostają. Wywołanie drugi raz nic nie zmienia.
- `Create(identityId, displayName, email, now)` — fabryka, stempluje niezmienny `ProvisionedAt`.

**Łańcuch referencji:** `Translation.SubmittedById/ApprovedById → Translator.Id (TranslatorId)`,
a `Translator.IdentityId → użytkownik Auth`. Jeden wiersz `Translator` zamiast kopii nazwy na każdym
`Translation`: po zmianie nazwy nic się nie rozjeżdża (ADR-0004).

---

## Maszyna stanów Translation — cykl aktualizacji (spec 0001)

**Jeden enum `TranslationStatus`**, bez osobnej flagi unieważnienia. Dzięki temu nie da się zapisać
nielegalnej kombinacji, np. „Approved i jednocześnie unieważniony". Unieważnienie *jest* przejściem do
`NeedsReview`.

```
                    CreateUntranslated (import: dodany wiersz)
                              │
                              ▼
                      ┌──────────────┐
                      │ Untranslated │
                      └──────┬───────┘
            ProvideTranslation (upsert)
                              ▼
   ┌──────────────────────▶ Draft ──────── Approve ────────▶ Approved
   │                          ▲                                  │
   │              ProvideTranslation (upsert)        ApplySourceChange (import:
   │                          │                       zmiana źródła nad pracą polską)
   │                          │                                  │
   └── ProvideTranslation ── NeedsReview ◀───────────────────────┘
        (re-translacja)        ▲   (unieważnienie: zamrożenie PreviousSourceText)
                               └── ApplySourceChange (kolejne zmiany źródła nadpisują
                                    Source, ale PreviousSourceText zostaje zamrożony)
```

- **Unieważnienie** = `Draft`/`Approved` → `NeedsReview` w `ApplySourceChange`. `PreviousSourceText`
  trzyma angielski, pod który napisano *obecny* polski. Kolejne zmiany źródła go nie nadpisują.
- **Approve** = `Draft`/`NeedsReview` → `Approved`. Czyści `PreviousSourceText` i wraca wiersz do
  pobieranego pliku.
- **Upsert** na `NeedsReview` → `Draft` (`PreviousSourceText` zostaje do akceptacji).
- **„Fallback do angielskiego":** angielski tekst nigdy nie trafia do pola polskiego. Unieważniony
  wiersz wypada z pobieranego pliku, więc `patch` nie nałoży nieaktualnego polskiego (spec 0001).

`GameVersionStatus` działa tak samo: jeden enum `Unprocessed · Processed · Superseded` zamiast boola
i flagi.

---

## Obiekty wartości (Value Objects)

Wszystkie dziedziczą po `SharedKernel.BuildingBlocks.ValueObject` (równość po `GetAtomicValues()`),
walidują w `Create(...)` przez `Result` i są niezmienne.

| VO | Plik | Reguła |
|---|---|---|
| `FragmentKey` | `…TranslationAggregate/ValueObjects/FragmentKey.cs` | `FileId > 0` i `GossipId >= 0`; tożsamość `(FileId, GossipId)`: `FileId` wskazuje subplik DAT, `GossipId` (8 bajtów) fragment w nim |
| `TranslationSource` | `…TranslationAggregate/ValueObjects/TranslationSource.cs` | `Text` + `ArgsOrder` + `ArgsId` jako **jedna** wartość (inny układ placeholderów = inne znaczenie); pusta kolumna argumentów i `NULL` dają `null`; tekst jest surowy, bez escape'u z pliku (ADR-0039) |
| `LotroNotationVersion` | `…GameVersionAggregate/ValueObjects/LotroNotationVersion.cs` | gramatyka `digits(.digits)*`, od jednego do trzech członów, max 12 znaków; **forma kanoniczna**: końcowe zera są obcinane, więc `48` = `48.0` = `48.0.0` (ADR-0003 z aneksem 2026-08-28) |
| `DisplayName` | `…TranslatorAggregate/ValueObjects/DisplayName.cs` | niepuste, ≤ **150** znaków (po trim) |
| `Email` | `…TranslatorAggregate/ValueObjects/Email.cs` | niepuste, ≤ **250**, regex `^[^@\s]+@[^@\s]+\.[^@\s]+$`; opcjonalny na `Translator` |

Strongly-typed ID (`TranslationId`, `GameVersionId`, `TranslatorId`, `PrecomputedTranslationFileId`)
leżą w `TranslationSystem.Primitives`; `IdentityId` (wspólny z Auth) leży w
`SharedKernel/StronglyTypedIds`. To ręcznie pisane `readonly record struct` z interfejsem
`IStronglyTypedId<T>`, bez pakietu ani generatora (ADR-0010). Nowe ID to **GUID v7**
(`Guid.CreateVersion7()`, uporządkowane w czasie). W JSON są **gołym stringiem GUID**
(`StronglyTypedIdJsonConverter<T>`).

---

## Serwisy domenowe

### TranslationDiffService

`src/TranslationSystem/…Domain/Aggregates/TranslationAggregate/Services/TranslationDiffService.cs`

Czysty diff (`static class`, `ComputePlanAsync`) wgranego `exported.txt` z zapisanym stanem źródeł,
po tożsamości fragmentu w całym pliku. Pracuje na wierszach wartości (spec 0006): upload przychodzi
jako mapa klucz → hash (`FragmentKeyValue` → `SourceHash`), katalog płynie jako strumień
`StoredSourceDigest`, a źródła porównuje się hash do hasha. Serwis nie trzyma więc ani tekstów
źródeł, ani agregatów. Zwraca `TranslationDiffPlan` **bez dotykania bazy**. Handler importu wykonuje
plan w swojej transakcji, gdy przejdzie guard truncacji. Pięć wyników:

| Wynik | Warunek | Akcja |
|---|---|---|
| **Added** | para nieznana | `CreateUntranslated` (samo źródło) |
| **Source-changed** | para znana, źródło różne | `ApplySourceChange` (unieważnia polski, jeśli był) |
| **Removed** | zapisana para nieobecna w uploadzie | `MarkRemoved` (miękko) |
| **Restored** | wcześniej usunięta para wraca z identycznym źródłem | `Restore` |
| **Unchanged** | para znana, źródło identyczne | nic |

**Echo (spec 0012).** Admin eksportuje z własnej, spatchowanej gry, więc wiersz z naszym polskim
wraca jako „źródło". Gdy przychodzący hash różni się od źródła, ale pasuje do
`StoredSourceDigest.EchoHash` (hash obecnego polskiego), wiersz liczy się jak niezmieniony (albo
przywrócony, jeśli był usunięty). `EchoedCount` służy tylko do raportu.

`TranslationDiffPlan` (`…/Services/TranslationDiffPlan.cs`) liczy też `RemovedFraction`: jaką część
aktywnych wierszy upload by usunął. Przy pierwszym imporcie nie ma aktywnych wierszy, więc wynosi 0
i guard truncacji nie zadziała. `HasPolish` (`Draft`/`Approved`/`NeedsReview`) decyduje, czy zmiana
źródła liczy się jako unieważnienie (`InvalidatedCount`).

---

## Projekcja: PrecomputedTranslationFile

`src/TranslationSystem/LotroKoniecDev.TranslationSystem.Projections/PrecomputedTranslationFile.cs`

Gotowy do pobrania plik `polish.txt`: wiersze `Approved` i nieusunięte, posortowane po `FileId`, potem
`GossipId`. Serwuje go `GET /api/v1/translation-files/{lang}` z ETagiem z treści (hex SHA-256).
ETag jest też hashem integralności dla patchera (AUDIT-SEC-01). Plik jest przebudowywany po każdym
zapisie, który zmienia zbiór dystrybucji. Pełne pobranie strumieniuje kopię z dysku API, zapisaną raz
na każdy hash treści (ADR-0064).

**Nie jest agregatem (ADR-0007 „read projections are not aggregates").** To `Entity`, nie
`AggregateRoot`: nie broni żadnej reguły, jest wyliczany i można go odbudować. Dlatego:

- żyje w osobnym projekcie `TranslationSystem.Projections` (Domain go nie referuje). Typ jest
  niezmienny i służy tylko do wstawienia pierwszego wiersza dla języka;
- zapisuje go osobny port `IPrecomputedTranslationFileStore` (`TryRefreshAsync` — jeden `UPDATE`
  w miejscu, bez ładowania starej, wielomegabajtowej treści (PERF-04) — i `Insert`), **nie**
  `IRepository`. Repozytoria są tylko dla agregatów. Implementacja:
  `…Persistence/Projections/PrecomputedTranslationFileStore.cs`;
- przebudowuje go `IPrecomputedTranslationFileProjector` **w tle, z debounce** (PERF-04, ADR-0021).
  Handler zapisu po commicie tylko woła `ITranslationFileRebuildScheduler.Schedule(...)`. Worker zbiera
  sygnały (okno domyślnie 2 s, `TranslationFileRebuild:DebounceWindow`) i robi jedną przebudowę naraz.
  Odpowiedź na zapis nie czeka, więc pobranie może chwilę „gonić" commit.

Strona odczytu (`PrecomputedTranslationFileReadModel`) i typ zapisu mapują się na **tę samą tabelę**
`TranslationArtifacts` (nazwa tabeli została po zmianie nazwy typu — ADR-0007).

---

## Repozytoria i Unit of Work

- `IRepository<TAggregateRoot, TId>` (`SharedKernel/BuildingBlocks/IRepository.cs`) jest **tylko dla
  agregatów** (`where TAggregateRoot : AggregateRoot<TId>`). Konkretnie: `ITranslationRepository`,
  `IGameVersionRepository`, `ITranslatorRepository` (interfejsy w Domain, implementacje w
  `…Persistence/DomainRepositories/` nad `GenericRepository`).
- `IUnitOfWork` (`…Persistence/DbContexts/Abstractions/IUnitOfWork.cs`) to punkt commitu.
  Implementuje go `ApplicationWriteDbContext`.
- `IPrecomputedTranslationFileStore` to osobny **Store** (nie repozytorium) dla projekcji
  (interfejs w `…Projections/IPrecomputedTranslationFileStore.cs`, implementacja w
  `…Persistence/Projections/PrecomputedTranslationFileStore.cs`). `Store` obok `Repository` to
  decyzja z ADR-0007, nie niespójność.

---

## CQRS — rozdział read/write (ADR-0002)

| | Zapis | Odczyt |
|---|---|---|
| Kontekst | `ApplicationWriteDbContext` (= `IUnitOfWork`, ma migracje) | `ApplicationReadDbContext` za `IApplicationReadDbContext` |
| Model | agregaty domenowe (zmienne) | płaskie read modele (`IReadOnlyEntity<TId>`) |
| Kto | command handlery (repozytoria + UoW) | query handlery (LINQ po read modelach) |

Read modele (`TranslationSystem.ReadModels`) i ich konfiguracje EF (`…ReadModels.EntityFramework`)
mapują się na **te same tabele** co model zapisu (`ToTable("Translations")` po obu stronach). To inny
typ na tym samym wierszu, nie osobna tabela. `TranslationReadModel` dołącza `SubmittedBy`/`ApprovedBy`
(`TranslatorReadModel`), żeby pokazać nazwy (ADR-0004). Query handler **nigdy** nie dotyka modelu
zapisu.

Tabele (PostgreSQL, schemat `translation`): `GameVersions`, `Translations`, `Translators`,
`TranslationArtifacts`.

---

## Obsługa błędów

- **Monady** `Result` / `Result<T>` / `Maybe<T>` (`SharedKernel.Monads`). Reguła biznesowa, która może
  zawieść, zwraca `Result.Failure(Error)`; brak wartości to `Maybe.None`.
- **`Error`** = `(Code, Message, Type)`. Fabryki w `DomainErrors.*` (`…Domain/Core/Errors/*`):
  `BaseDomainErrors` (prywatne helpery, np. `Required`, `TooManyCharacters`, `AlreadyHasBeenTaken`,
  `InvalidOperation`, `HasNotBeenFound`, `TooManySegments`) i pliki per agregat
  (`TranslationAggregateDomainErrors`, `GameVersionAggregateDomainErrors`,
  `TranslatorAggregateDomainErrors`).
- **Mapowanie na HTTP** (`…API/Extensions/ErrorExtensions.cs`): `Validation→400`, `NotFound→404`,
  `Forbidden→403`, `DataConflict→422`, `TooManyRequests→429`, reszta→`500`; jako `ProblemDetails`
  z `errorCode`. Szczegóły: [API.md §Error contract](API.md#5-error-contract-problemdetails).
- **Guardy** (`Ensure.NotEmpty`, `ArgumentNullException.ThrowIfNull`,
  `ArgumentException.ThrowIfNullOrWhiteSpace`) rzucają wyjątek. Są tylko na błędy programisty.

Pełny katalog reguł z lokalizacjami: [INVARIANTS.md](INVARIANTS.md).

---

## Struktura projektu

```
src/TranslationSystem/
  LotroKoniecDev.TranslationSystem.Primitives                 # strongly-typed ID + enumy (bez zależności domenowych)
  LotroKoniecDev.TranslationSystem.Domain                     # agregaty, VO, serwisy domenowe, DomainErrors
  LotroKoniecDev.TranslationSystem.ReadModels                 # płaskie read modele
  LotroKoniecDev.TranslationSystem.ReadModels.EntityFramework # konfiguracje EF read modeli (te same tabele)
  LotroKoniecDev.TranslationSystem.Projections                # PrecomputedTranslationFile + IPrecomputedTranslationFileStore
  LotroKoniecDev.TranslationSystem.Persistence                # Write/Read DbContext, repozytoria, konfiguracje EF, migracje
  LotroKoniecDev.TranslationSystem.Contracts                  # DTO request/response (używane przez Frontend)
  LotroKoniecDev.TranslationSystem.API                        # endpointy VSA (Features/<Obszar>/<Akcja>.cs), Auth, HATEOAS, parsing
```

## Wzorce projektowe

- **Vertical Slice Architecture** — spójność per feature zamiast warstw technicznych.
- **Slim SRP handlers bez mediatora (ADR-0001)** — rekord + handler + walidator, jawna rejestracja
  zamkniętego interfejsu w `…API/ApiDependencyInjection.cs`.
- **CQRS read/write split (ADR-0002).**
- **Value Object + strongly-typed ID** — brak prymitywnej obsesji.
- **Result/Maybe monad** — błędy jako wartości.
- **Projekcja do odczytu (ADR-0007)** — projekcja ≠ agregat; `Store` ≠ `Repository`.
- **Przebudowa w tle z debounce (ADR-0021)** — zapis daje sygnał, worker zbiera sygnały i przebudowuje
  artefakt.
- **Leniwe, idempotentne tworzenie profilu (ADR-0004, aneks 2026-06-24)** — profil tłumacza powstaje
  przy pierwszym uwierzytelnionym żądaniu, a wynik jest w cache.
