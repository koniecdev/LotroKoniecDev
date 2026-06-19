# Invarianty — skrót (slim, always-on)

> Skrót **wszystkich** reguł domeny TMS. Pełna wersja z lokalizacjami `plik:linia`, kontekstem i listą
> rozbieżności: **[`docs/INVARIANTS.md`](INVARIANTS.md)** — czytaj ją, gdy potrzebujesz „gdzie/jak"
> albo modyfikujesz Domain. **Kod jest źródłem prawdy** — przy rozbieżności czytaj kod i popraw oba.
> 🟢 twardy invariant w domenie (nie do obejścia) · 🔵 reguła aplikacji (handler/validator/konfig) · ⚠️ kontrintuicyjne.

## 0. Przekrojowe
- Errors-as-values: reguły biznesowe ⇒ `Result.Failure(Error)`/`Maybe`. `Ensure.*`/`ArgumentNullException`/`ArgumentException` = błędy programisty (rzucają).
- Brak prymitywnej obsesji: każdy koncept z ograniczeniem/tożsamością = `ValueObject` lub strongly-typed ID (GUID v7, serializowany jako goły string).
- Walidacja string-VO: **pustość → trim → długość → format**. Same spacje ⇒ `NullOrEmpty`/`InvalidFormat`, nigdy `LongerThanAllowed`.
- Każda mutacja stempluje znacznik czasu (`UpdatedAt`/`LastSeenAt`/`GeneratedAt`) z `TimeProvider`.
- Endpointy **autoryzowane domyślnie** (fallback policy); publiczne jawnie `AllowAnonymous`. Atrybucja użytkownika z tokena, nigdy z body.
- Walidatory **tylko dla komend**; zapytania walidują się inline w handlerze.
- Mapowanie błędów na HTTP: `Validation→400`, `NotFound→404`, `Forbidden→403`, `DataConflict→422`, reszta→500; kod w rozszerzeniu `errorCode` (`ProblemDetails`).

## 1. Translation — stany
Statusy: `Untranslated, Draft, Approved, NeedsReview` (+ `Unset` nigdy). Rodzi się **Untranslated**. **Jeden enum, bez równoległej flagi unieważnienia** — unieważnienie *jest* przejściem do `NeedsReview` (spec 0001 Q6).
- 🟢 `FragmentKey` (tożsamość `(FileId, GossipId)`) **niezmienny**, stabilny ponad wersjami gry.
- 🟢 `ProvideTranslation`: **każdy** poprzedni status → `Draft`, stempluje submittera; `PreviousSourceText` nietknięty. Edycja `Approved` wyciąga wiersz z dystrybucji.
- 🟢 `Approve` (z `Draft`/`NeedsReview`): wymaga polskiego (`CannotApproveWithoutTranslation`) + nieusunięcia (`CannotApproveRemoved`); → `Approved`, stempluje approvera, **czyści** `PreviousSourceText`.
- 🟢 `ApplySourceChange`: nadpisuje `Source`; jeśli był `Draft`/`Approved` ⇒ **unieważnienie** (zamraża `PreviousSourceText`, → `NeedsReview`); czyści miękkie usunięcie.
- 🟢 ⚠️ `PreviousSourceText` **zamrożony** — kolejne zmiany źródła na `NeedsReview` go nie nadpisują (translator widzi angielski, do którego pisał wciąż obecny polski).
- 🟢 `MarkRemoved` = miękkie usunięcie (`IsRemoved => RemovedInVersion is not null`); `Restore` czyści usunięcie, **zachowuje** poprzedni status (w tym `Approved`).

## 2. Translation — wartości
| VO | Reguła |
|---|---|
| FragmentKey | `FileId > 0`, `GossipId >= 0` (⚠️ `0` legalny — mirror parsera patchera) |
| TranslationSource | `Text` + `ArgsOrder` + `ArgsId` jako **jedna** wartość (równość obejmuje args); blank/`NULL` w args → `null`; puste źródło legalne |
- ⚠️ Zmiana struktury argumentów = zmiana znaczenia (diff traktuje jako source-change nawet bez zmiany tekstu).

## 3. GameVersion
Statusy: `Unprocessed, Processed, Superseded` (+ `Unset` nigdy). Rodzi się **Unprocessed**.
- 🟢 `Superseded` **nigdy** nie przetworzony (`SupersededCannotBeProcessed`). `Processed` **nie cofa się** do `Superseded` (`ProcessedCannotBeSuperseded`).
- 🟢 `LotroNotationVersion`: niepuste, ≤ **12** znaków (surowych), gramatyka `digits(.digits)*`, **forma kanoniczna** — `48`=`48.0`=`48.0.0` (zera końcowe zwijane, wiodące usuwane; wewnętrzne znaczące). ADR-0003.
- 🟢 ⚠️ `DetectedAt` = porządek wersji (forum chronologiczne); **brak parsowania semver**.

## 4. Translator (ADR-0004)
- 🟢 `IdentityId` (cross-context FK do Auth) niezmienny, wymagany. `RefreshProfile` zbiega `DisplayName`/`Email` z claimów na każde dotknięcie; `null` email czyści.
- 🔵 `Translators.IdentityId` **unikalny** (klucz idempotencji); prowizjonowanie **leniwe + idempotentne** na pierwszym zapisie (get-or-create; wyścig ⇒ re-read po `DbUpdateException`).
- 🔵 ⚠️ `DisplayName` = claim `name`, fallback `email`; brak obu ⇒ błąd. Email malformowany ⇒ brak emaila (nie wywala zapisu).
- 🟢 `DisplayName` niepuste ≤ **150**; `Email` (opcjonalny) niepuste ≤ **250** + regex `^[^@\s]+@[^@\s]+\.[^@\s]+$`.

## 5. PrecomputedTranslationFile (ADR-0007)
- 🟢 ⚠️ `Entity`, **nie** `AggregateRoot` — nie broni invariantu; za `Store`, nie `Repository`. Jeden wiersz na język (klucz naturalny, upsert).
- 🔵 Plik = **Approved + nieunieważnione + nieusunięte**, sort `FileId`→`GossipId`; kolumna `approved` zawsze `1`, CRLF; byte-compatible z patcherem. Regenerowany po każdym zapisie zmieniającym zbiór dystrybucji.

## 6. TranslationDiffService (serwis domenowy)
- 🟢 Czysta funkcja, kluczowana po `FragmentKey`, **bez dotykania DB**. Pięć rozłącznych wyników: Added · Source-changed · Removed · Restored · Unchanged.
- 🟢 Unieważnienie liczy się tylko gdy wiersz **miał polski** (`HasPolish` = Draft/Approved/NeedsReview). `RemovedFraction = 0` dla baseline (guard truncacji nie tripuje na pierwszym wczytaniu).

## 7. Import (`POST /game-versions/{id}/import`, admin)
Wszystko w jednej transakcji (all-or-nothing, idempotentny re-upload); błędy = `DataConflict`/422.
- 🔵 `GameVersionId` niepusty + `FileStream` nienull (walidator). Nieznana wersja → 404.
- 🔵 Odrzuca: błędy parsowania (`ParseFailed`), pusty upload (`EmptyUpload`), niepoprawny wiersz (`InvalidRow`), zduplikowany `FragmentKey` (`DuplicateFragmentKey`).
- 🔵 ⚠️ Masowe usunięcie > **20%** aktywnych bez `allowMassRemoval=true` ⇒ `MassRemovalBlocked`.
- 🔵 `MarkAsProcessed` w **tej samej** transakcji co diff; potem rebuild artefaktu.

## 8. Translations (aplikacja)
- 🔵 Upsert: `FileId>0`, `GossipId>=0`, `TranslatedText` niepuste (walidator). Nieistniejący fragment → 404; miękko usunięty → 422 (`CannotEditRemoved`). Prowizjonuje submittera **przed** FK; edycja `Approved` ⇒ rebuild artefaktu.
- 🔵 Approve: `Id ≠ Empty` (walidator); nieznany → 404; prowizjonuje approvera; rebuild po sukcesie.
- 🔵 Lista: nieobsługiwany `lang` → 400; **wyklucza** miękko usunięte; sort `(FileId, GossipId)`; `pageSize` clamp **1–100**; ⚠️ search escapuje metaznaki LIKE (ILIKE, escape `\`).
- 🔵 Get-one: id `Empty` → `NotFound` (short-circuit). Stats: `Translated` = Draft+Approved+NeedsReview, `Remaining = Total - Approved`, po aktywnym katalogu.

## 9. Game versions (aplikacja)
- 🔵 Register: `Version` niepuste ≤ **12**; duplikat (po kanonikalizacji) → 422; **201** z `Location` na item-endpoint.
- 🔵 Lista niestronicowana, sort `DetectedAt` malejąco. Get-one: id `Empty` → `NotFound`.

## 10. Dystrybucja (`GET /translation-files/{lang}`, anonimowy)
- 🔵 `lang` musi być `polish` (inaczej 400); brak artefaktu → 404; ETag = content hash, `If-None-Match` (lub `*`) ⇒ **304**; `Cache-Control: private, no-cache`.

## 11. Auth (AuthSystem — lift)
- 🔵 Hasło **8–128** + ≥1 cyfra/mała/wielka/specjalny; Identity `RequiredLength=8` (⚠️ bez górnego limitu na poziomie Identity).
- 🔵 Email unikalny ≤ 250 regex; Username ≤ 150; Phone **wymagany** ≤ 30. Zgody privacy + data-processing **muszą być true**.
- 🔵 ⚠️ Nowy user ⇒ rola **`Translator`** (role: `Admin`/`Translator`); seedowany admin ⇒ `Admin`.
- 🔵 Email confirmation **wymagane do logowania**; lockout **5 prób / 5 min**. Tokeny: access **60 min**, refresh **14 dni** (referencyjne, rolling, rewokowalne); email/reset **24 h**.
- 🔵 Anti-enumeration (Forgot/Resend zawsze sukces; dummy-hash przy braku usera). Produkcja = auth code + **PKCE**, klucze RSA-2048/AES-256 walidowane; password flow tylko w `Testing`.
- 🔵 ⚠️ **Brak sagi rejestracji** — rejestracja tworzy tylko usera Auth; profil `Translator` prowizjonowany leniwie na pierwszym zapisie TMS (ADR-0002 §7 / ADR-0004).
- 🔵 `DeleteAccount` = erasure RODO + **permanentny lockout** (wymaga hasła).
