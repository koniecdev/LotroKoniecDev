# Audyt #0002 — Overengineering / jawne naruszenia YAGNI

> **Soczewka audytowa: gdzie w systemie jest kod, konfiguracja lub infrastruktura zbudowana na
> zapas — bez realnej, obecnej potrzeby.** Zakres: cały monorepo (Patcher, TMS, AuthSystem,
> Frontend, Utilities, SharedKernel, infra/CI/skrypty).
>
> **Metoda.** 4 niezależne deep-dive'y (TMS backend · Auth+Frontend+Utilities · Patcher · infra/CI),
> każdy z obowiązkiem aktywnej falsyfikacji findingu (szukanie konsumenta, testu-seamu, ADR-a lub
> zapisu w CLAUDE.md, który czyni rzecz zamierzoną). Następnie **każdy kluczowy finding
> zweryfikowany niezależnie drugim przebiegiem grep/read** — do raportu weszły tylko te, które
> przeżyły obie rundy. Kandydaci odrzuceni jako false positive są wylistowani na końcu (sekcja
> „Odrzucone"), żeby następny audyt nie odkrywał ich od nowa.
>
> Świadome decyzje architektoniczne (lift 1:1 z TheKittySaver, CQRS od dnia 1, ReadModels od
> dnia 1, strongly-typed IDs, debounce artefaktu ADR-0021, dormancja `RequireServiceScope`
> ADR-0032, stack supply-chain, dwustopniowa promocja itd.) **nie są findingami** — patrz
> „Odrzucone".

**Status:** Informacyjny (raport, nie decyzja — ewentualne sprzątanie to osobne tickety/ADR)
**Data:** 2026-07-18
**Gałąź audytu:** `claude/system-overengineering-audit-lluj42`

---

## Ocena ogólna

System jest — jak na swoją skalę — **zdyscyplinowany**: zdecydowana większość abstrakcji ma
realnych konsumentów, knoby konfiguracyjne są czytane i testowane, a ciężkie mechanizmy
(HybridCache, debounce, hash-only ETag path, supply-chain w CD) mają udokumentowane, przypięte
decyzje. Znalezione naruszenia YAGNI układają się w **cztery powtarzalne wzorce**, nie w losowy
szum:

1. **Osad liftu z TheKittySaver** — powierzchnia API/utility przeniesiona 1:1, której w tym repo
   nic nie konsumuje (W1, W7, T1–T3, T6, F7).
2. **Rezydua uproszczonego flow launch/preflight w Patcherze** — flow uproszczono (knowledge
   base), ale jego spekulatywne wyjścia, seam strategii i katalog błędów zostały (P2, P3, P6, P8).
3. **Testy pokrywające martwy kod** — orphan testy sprawiają, że martwa powierzchnia przechodzi
   sniff-test „przecież jest testowane" (P1, P4, P10, T2).
4. **Instalacja pod vaporware** — env-vary, mounty i wiersze runbooka pod feature, który nigdy
   nie powstał (I1, I5).

Nic z poniższego nie jest krytyczne. Koszt utrzymania to głównie szum poznawczy, mylący output
(P2), fałszywy sygnał „ALL TESTS PASSED" (I2) i utrzymywanie martwych testów.

---

## Findingi

Konwencja: **[pewność]** po samodzielnej re-weryfikacji · dowody `plik:linia` · jednolinijkowa
propozycja uproszczenia. Prefiksy: P=Patcher, T=TMS, A=Auth/Frontend, W=Utilities/wspólne,
I=infra/CI.

### Patcher

**Zastrzeżenie remediacyjne:** patcher jest *stable* — usunięcie martwej powierzchni pociąga
usunięcie jej orphan-testów. To zgodne z duchem poprawki ADR-0002 (test ginie *razem* z kodem,
żadna asercja żywego testu się nie zmienia), ale każdy taki ruch powinien być osobnym, małym PR;
P5 dodatkowo przez `dat-format-expert`.

#### P1 — `ResultExtensions`: cała klasa kombinatorów bez ani jednego produkcyjnego wywołania **[wysoka]**
- `src/Patcher/LotroKoniecDev.Domain/Core/Extensions/ResultExtensions.cs:9-108` — 8 metod
  (`OnSuccess`, `OnFailure`, `Map`, `Bind`, `GetValueOrDefault`, `Match`, `ToResult`, `Combine`).
- Grep repo-wide: 0 użyć poza samą klasą i jej testem
  (`tests/LotroKoniecDev.Tests.Unit/Tests/Extensions/ResultExtensionsTests.cs`). Cały kod
  produkcyjny używa wyłącznie stylu `if (result.IsFailure) return …`; TMS ma własną kopię w
  SharedKernel. Obrona „M4 tego użyje" pada: M4 reużywa handlery, które kombinatorów nie używają,
  a mirror-rule kazałby nowemu kodowi naśladować styl istniejących handlerów.
- *Uproszczenie:* usunąć klasę + jej plik testów.

#### P2 — `GameLaunchingResponse`: trzy pola na stałe `null`/`false`/`0` + mylący `ToString` **[wysoka]**
- `src/Patcher/LotroKoniecDev.Application/Features/GameLaunching/GameLaunchingResponse.cs:3-6,17-22`;
  jedyny producent hardkoduje `ForumVersion: null, UpdateWasDetected: false, GameExitCode: 0`
  (`SimplifiedGameLaunchingStrategy.cs:167-171` — zweryfikowane bezpośrednio).
- Pozostałość po wycofanym flow z detekcją update'u i czekaniem na exit gry. Gałąź
  `"Game updated to version …"` jest nieosiągalna, a `"Session ended (exit code 0)"` przy
  fire-and-forget launchu jest **aktywnie mylące** (proces nigdy nie jest obserwowany).
- *Uproszczenie:* `GameLaunchingResponse(bool TranslationsApplied, int AppliedCount, int SkippedCount)` + poprawić `ToString`.

#### P3 — `IGameLaunchingStrategy`: wzorzec strategii z jedną strategią na zawsze **[średnio-wysoka]**
- `src/Patcher/LotroKoniecDev.Application/Abstractions/IGameLaunchingStrategy.cs:5`;
  pass-through handler `GameLaunchingCommandHandler.cs:9-36`; jedyna implementacja
  `SimplifiedGameLaunchingStrategy.cs:10`; DI `ApplicationDependencyInjection.cs:39`.
- W całej historii gita nigdy nie istniała druga strategia; „Simplified" w nazwie odnosi się do
  flow wycofanego *zanim* kod się ustabilizował. To nie jest granica (czysta logika aplikacyjna —
  prawdziwymi granicami są porty, które wstrzykuje). Mockowanie tej „strategii" w testach handlera
  to dokładnie zakazane przez własną filozofię testową repo „stubowanie internali, które posiadasz".
- *Uproszczenie:* wchłonąć strategię do handlera, usunąć interfejs (scala dwie klasy testowe bez
  utraty pokrycia).

#### P4 — `Translation.GetUnescapedContent()`: martwy duplikat unescape'u parsera — użyty, zepsułby dane **[wysoka]**
- `src/Patcher/LotroKoniecDev.Domain/Models/Translation.cs:40-41`; duplikat
  `TranslationFileParser.UnescapeContent` (`Parsers/TranslationFileParser.cs:110-111`), który
  działa już w czasie parsowania (`:88`).
- Zweryfikowane: jedyne referencje to własne testy (`TranslationTests.cs:150-179`). Content w
  `Translation` jest już odescape'owany — wywołanie tej metody na realnych danych **podwójnie**
  odescape'owałoby literalne `\r`/`\n`. Martwy kod, który jest jednocześnie pułapką.
- *Uproszczenie:* usunąć metodę + 2 testy.

#### P5 — `SubFile.Serialize(argsOrder, argsId, targetFragmentId)`: martwa równoległa maszyneria reorderu argumentów **[wysoka]**
- `src/Patcher/LotroKoniecDev.Domain/Models/SubFile.cs:76,88-93,112` (opcjonalne parametry, gałąź
  `targetFragmentId`, prywatne `ReorderArguments`).
- Zweryfikowane: wszystkie 3 call-site'y repo-wide (`PatchingService.cs:91,160`,
  `SubFileTests.cs:184`) wołają gołe `Serialize()` — gałąź jest nieosiągalna. Produkcyjny reorder
  idzie przez `Fragment.TryReorderArgRefs` (`PatchingService.cs:137`). Powiązane:
  `Translation.ArgsId` (`Translation.cs:14`) jest parsowane i przechowywane, ale nic go nie
  konsumuje — ta martwa ścieżka była jego zamierzonym konsumentem. Kolumna `args_id` w pliku `||`
  zostaje (kontrakt formatu — parser musi ją przyjmować).
- *Uproszczenie:* `Serialize()` bezparametrowe, usunąć `ReorderArguments`; edycja przez
  `dat-format-expert` (obszar DAT).

#### P6 — Preflight liczy `IsGameRunning`/`HasWriteAccess` i wyrzuca wyniki do kosza **[średnia]**
- `src/Patcher/LotroKoniecDev.Application/Features/PreflightChecking/PreflightReportResponse.cs:6-7`;
  liczone w `PreflightCheckQueryHandler.cs:39-42`; jedyny produkcyjny konsument
  (`PatchCommand.cs:114-116`) czyta **tylko** `ForumVersion`. Łańcuch
  `IWriteAccessChecker`→`WriteAccessChecker` istnieje wyłącznie po to, by wyprodukować porzucony bool.
- Zweryfikowane: zero konsumentów obu pól poza slice'em i asercjami testów. Patch przebiega
  identycznie niezależnie od tego, czy gra działa i czy katalog DAT jest zapisywalny — to albo
  YAGNI, albo **utajony brak guardu** (fail-fast, który miał być, a nie jest). Żaden ticket M4
  (#41–#46) nie specyfikuje ekranu preflight.
- *Uproszczenie:* albo zacząć działać na tych boolach w `PatchCommand` (fail-fast), albo zwęzić
  preflight do fetchu wersji forum i usunąć łańcuch `IWriteAccessChecker`. Decyzja właścicielska.

#### P7 — CLI `--verbose`: knob, którego nic nie czyta **[wysoka]**
- `src/Patcher/LotroKoniecDev.Cli/Commands/GlobalSettings.cs:21-24` — jedyny hit repo-wide to
  deklaracja (zweryfikowane). Widoczny w `--help` każdej komendy, obiecuje coś, czego nie robi;
  verbosity jest na stałe w konfigu Serilog (`Program.cs:19-25`, design AUDIT-SEC-07).
- *Uproszczenie:* usunąć opcję albo podpiąć pod minimum level sinka konsolowego (1 linia w obie strony).

#### P8 — Osiem martwych fabryk `DomainErrors` **[wysoka]**
- `src/Patcher/LotroKoniecDev.Domain/Core/Errors/InfrastructureDomainErrors.cs:12-13,22-27,61-67`
  (`Backup.CannotRestore`, `DatFileLocation.GameRunning`, `DatFileLocation.NoWriteAccess`,
  `GameUpdateCheck.VersionNotFoundInPage`, `GameUpdateCheck.GameUpdateRequired`);
  `DatFileDomainErrors.cs:24-28,36-38` (`SubFile.NotFound`, `SubFile.NotTextFile`, `Fragment.NotFound`).
- Zweryfikowane per fabryka: 0 konsumentów (hity w testach to wyłącznie *nazwy metod* testowych).
  Katalog błędów zbudowany na wyrost: część należy do wycofanego blokującego flow update'u, część
  wyparły celowe warning-stringi `PatchingService` (`:75,82,153`), para `GameRunning`/`NoWriteAccess`
  wisi na porzuconych boolach z P6. `ErrorMapper` mapuje po `ErrorType`, nie potrzebuje
  konkretnych fabryk.
- *Uproszczenie:* usunąć osiem fabryk (skoordynować z decyzją w P6).

#### P9 — `Maybe<T>`: pełny kontrakt równości, którego nic nie wykonuje **[średnio-wysoka]**
- `src/Patcher/LotroKoniecDev.Domain/Core/Monads/Maybe.cs:7,41,45,47-77` — `IEquatable<>`, dwa
  `Equals`, `GetHashCode`, `implicit operator T?`, publiczne `From` (~35 z 77 linii).
- Jedyny seam użycia (`BoundedResponseReader` → downloader/fetcher) konsumuje tylko `None`,
  implicit-from-value, `HasNoValue`, `Value`. Maszyneria równości ma zero konsumentów i — w
  odróżnieniu od `Result` — zero testów. Obecność samego `Maybe` jest zamierzona (CLAUDE.md);
  finding dotyczy wyłącznie nieużywanych składowych.
- *Uproszczenie:* wyciąć kontrakt równości i konwersję do `T?`.

#### P10 — Drobne martwe składowe (zgrupowane) **[wysoka, trywialne]**
- `Application/OperationProgress.cs:3-5` — parametr `Message` nigdy nie przekazywany, `IsCompleted`
  nigdy nie czytane; `Cli/ConsoleWriter.cs:13-18,34-35` — `WriteSuccess`, `WriteProgress` nigdy
  nie wołane; `Domain/Models/Fragment.cs:31` — `GetFullText` bez produkcyjnego użycia (eksport
  łączy `Pieces` bezpośrednio, `ExportTextsQueryHandler.cs:77`); `Domain/Models/Translation.cs:20`
  — `HasArguments` bez produkcyjnego użycia (patching sprawdza `ArgsOrder is not null` +
  `fragment.HasArguments`, `PatchingService.cs:135`). Wszystko z orphan-testami.
- *Uproszczenie:* usunąć składowe + ich testy.

### TMS backend

#### T1 — Endpoint Discovery: dokument odkrywania, który odkrywa wyłącznie sam siebie — i nikt go nie woła **[średnio-wysoka]**
- `TranslationSystem.API/Features/Discovery/Discovery.cs:9-25`;
  `TranslationSystem.API/Hateoas/DiscoveryFactories/DiscoveryLinkFactory.cs:16-27` (zweryfikowane:
  emituje **wyłącznie** link `self`); `IDiscoveryLinkFactory.cs`; DTO
  `Contracts/Discovery/DiscoveryResponse.cs`; DI `ApiDependencyInjection.cs:47`.
- Zweryfikowane bezpośrednio: frontendowa noga
  `DiscoveryCache.GetTranslationSystemDiscoveryAsync` (`Frontend/Infrastructure/Discovery/DiscoveryCache.cs:49`)
  ma **zero wywołań** (cache konsumuje tylko `AccountLoader`, wyłącznie nogą auth);
  `ITranslationSystemClient.GetDiscoveryAsync` jest wołane tylko z tej martwej nogi. CLI hardkoduje
  `api/v1/translation-files/pl` (`Patcher/…/Network/TranslationFileDownloader.cs:27`). Kontrast:
  **auth-owy** discovery jest żywy (noga `ExportAccountData` w GDPR-export). Jedyna realna rola
  TMS-owego roota to kanarek w `AuthorizationDefaultsTests` — tę rolę może pełnić dowolny endpoint.
- *Uproszczenie:* usunąć endpoint + fabrykę + DTO + martwą nogę frontendu; przepiąć test
  authorization-defaults na np. `GET /api/v1/translations`.

#### T2 — `PaginationAndMultipleSorting`: martwy publiczny rekord kontraktu **[wysoka]**
- `TranslationSystem.Contracts/Common/PaginationAndMultipleSorting.cs:3-8`.
- Zweryfikowane: zero referencji w src/tests/Frontend — oba list-query deklarują `Page/PageSize/Sort`
  inline. Osad liftu.
- *Uproszczenie:* usunąć plik.

#### T3 — `IPaginationable`/`ISortable`: marker-interfejsy bez polimorficznego konsumenta **[średnia]**
- `TranslationSystem.Contracts/Common/IPaginationable.cs`, `ISortable.cs`; implementowane w
  `ListTranslations.cs:46`, `ListGameVersions.cs:29` (i przez martwe T2).
- Zweryfikowane: żadna metoda, walidator, extension ani generic-constraint nie przyjmuje tych
  interfejsów (`ApplyPagination`/`ApplyMultipleSorting` biorą surowe int/string). Usunięcie klauzul
  implementacji kompiluje się identycznie.
- *Uproszczenie:* usunąć oba interfejsy i klauzule.

#### T4 — `AuthorizationPolicies.ApiScope`: polityka zarejestrowana, nigdy nie zastosowana **[niska — kandydat na dormancję à la ADR-0032]**
- `TranslationSystem.API/Auth/AuthorizationPolicies.cs:8`; rejestracja
  `ApiDependencyInjection.cs:283-285`. Zweryfikowane: zero `RequireAuthorization(AuthorizationPolicies.ApiScope`.
- Scope `api` jest żywy (mintowany przez auth, obecny w tokenach) — martwa jest sama *polityka*.
  ADR-0032 przypina dormancję siostrzanego `RequireServiceScope`, ale `ApiScope` nie wymienia —
  prawdopodobnie ta sama intencja, nieudokumentowana. Znalezione niezależnie przez dwa przebiegi.
- *Uproszczenie:* albo faktycznie egzekwować scope (dopiąć do fallback policy), albo usunąć /
  dopisać do listy dormancji ADR-0032 jedną linią.

#### T5 — `EnvironmentsExtensions`: nieosiągalne/nieużywane składowe — w obu bliźniakach (TMS i Auth) **[wysoka]**
- `TranslationSystem.API/Extensions/EnvironmentsExtensions.cs:5-11` oraz identyczny bliźniak
  `AuthSystem.API/Extensions/EnvironmentsExtensions.cs:5-11` (zweryfikowane oba).
- `ProductionName` — zero użyć; extension property `Environments.Development` jest **nieosiągalne**
  (frameworkowe statyczne pole `Environments.Development` zawsze wygrywa lookup) → martwe wraz z
  jedynym feederem `DevelopmentName`. Żywe są tylko `TestingName` + property `Testing`.
- *Uproszczenie:* w obu plikach zostawić tylko `TestingName` + `Testing`.

#### T6 — `IRepository.ExistsAsync`: martwa powierzchnia generycznego repozytorium **[średnia]**
- `SharedKernel/BuildingBlocks/IRepository.cs:10`; `TranslationSystem.Persistence/GenericRepository.cs:33-41`.
- Zweryfikowane: zero wywołań repo-wide (jedyne hity `ExistsAsync` to Identity `RoleExistsAsync`
  i prywatny helper testowy). Kontrargument to wierność liftowi — ale reguła repo brzmi „code wins"
  i YAGNI jest house rule.
- *Uproszczenie:* usunąć z interfejsu i implementacji.

### AuthSystem / Frontend

#### A1 — Cztery endpointy JSON duplikujące strony Razor — bez żadnego produkcyjnego klienta **[średnio-wysoka]**
- `AuthSystem.API/Features/Auth/ForgotPassword.cs:113`, `ResetPassword.cs`,
  `ResendEmailConfirmation.cs`, `CancelAccountDeletion.cs:165` (tu tylko `MapEndpoint` —
  Command/Handler jest potrzebny stronie Razor); osierocone DTO w
  `AuthSystem.Contracts/Features/Auth/` (`ForgotPasswordRequest`, `ResetPasswordRequest`,
  `ResendEmailConfirmationRequest`, `CancelAccountDeletionRequest`, `CancelAccountDeletionResponse`).
- Zweryfikowane bezpośrednio: każdy flow kontowy istnieje podwójnie (strona Razor — ta, której
  używa przeglądarka i do której celują **wszystkie** fabryki linków mailowych — plus równoległy
  endpoint JSON). Frontend woła tylko discovery, `export-account-data`, `change-password`,
  `delete-account`; E2E `cancel-deletion-submit` to testid **strony Razor**
  (`AuthSystem.API/Pages/Account/CancelDeletion.cshtml:343`), nie endpointu. Jedyni konsumenci
  czwórki to jej własne testy integracyjne. Kontrast zamierzony: `auth/register` i
  `auth/confirm-email` **zostają** — są realną infrastrukturą testową cross-suite
  (`UserFactory`, E2E `AuthApiClient`). ADR-0032 sam ustanowił baseline „frontend jest jedynym
  klientem".
- *Uproszczenie:* usunąć 4 `MapEndpoint`y/klasy endpointów (zachowując handlery współdzielone ze
  stronami) + 5 DTO + ich testy endpointowe; wyrównać `docs/API.md`.

#### A2 — Anonimowe linki discovery `register`/`forgot-password` reklamowane nikomu **[średnia]**
- `AuthSystem.API/Hateoas/DiscoveryFactories/DiscoveryLinkFactory.cs:35-45`.
- Gałąź anonimowa reklamuje rel-e celujące w endpointy z A1. Frontend konsumuje z auth-discovery
  wyłącznie `ExportAccountData` (`DiscoveryCache.cs:149`, `AccountLoader.cs:49`); hity
  `Rels.Register` w `GameVersions.razor`/`ImportExport.razor` to **TMS-owa** klasa Rels, nie auth
  (zweryfikowane). Anonimowy użytkownik trafia do rejestracji przez challenge OIDC → strony Razor.
- *Uproszczenie:* zwęzić gałąź anonimową do `self` (naturalnie razem z A1).

#### A3 — `AccountAggregateLinkFactory`: gałęzie stanów, których realny klient nie może zaobserwować **[średnia]**
- `AuthSystem.API/Hateoas/AccountAggregateFactories/AccountAggregateLinkFactory.cs:29-38,53-60`.
- `resend-email-confirmation`: przy `SignIn.RequireConfirmedEmail = true`
  (`PersistenceDependencyInjection.cs:50`) żaden bearer-klient nie może mieć niepotwierdzonego
  maila (poza Testing-only password grantem). `cancel-deletion`: zaplanowanie usunięcia unieważnia
  sesje i tokeny (własny komentarz frontendu w `DeleteAccount.razor:32`), więc nikt nie pobierze
  envelope w tym stanie — realny cancel idzie mailowym one-time linkiem → strona Razor (ADR-0031).
  Frontend konsumuje z envelope tylko `ChangePassword`/`DeleteAccount` (`Account.razor:179,190`).
- *Uproszczenie:* zredukować fabrykę do self + change-password + delete-account; usunąć dwa
  martwe rel-e z `Rels.cs`.

#### A4 — Typed-clienty frontendu: spekulatywne pass-throughy czasowników **[wysoka]**
- `Frontend/Infrastructure/HttpClients/TranslationSystemHttpClients/ITranslationSystemClient.cs`
  + `TranslationSystemClient.cs:55,71,80,97`; `IAuthSystemClient.cs`/`AuthSystemClient.cs:43`;
  `HttpClientApiExtensions.cs` (`PatchApiResultAsync`, bezbody-owe `PutApiResultAsync`).
- Zweryfikowane: zero call-site'ów dla niegenerycznego `PutApiResultAsync(uri, body)`,
  bezbody-owego `PutApiResultAsync(uri)`, `PatchApiResultAsync` (TMS API **w ogóle nie ma**
  endpointu PATCH), niegenerycznego `SendMultipartApiResultAsync`, generycznego
  `PostApiResultAsync<T>` na kliencie auth. Trywialne do ponownego dodania przy realnym endpoincie.
- *Uproszczenie:* usunąć 5 składowych interfejsów + implementacje + 2 osierocone extensiony.

#### A5 — Negocjacja dual-representation w Hateoas: gałąź plain-JSON bez konsumenta **[średnia — do decyzji produktowej]**
- `Utilities/LotroKoniecDev.Hateoas/ContentNegotiation/HateoasContentNegotiator.cs` (pełny parsing
  quality-factorów RFC 9110), `HateoasJsonTypeInfoModifiers.cs` (chirurgiczne tłumienie klucza
  `links`), `HateoasNegotiatedResult.cs` (żonglerka `Vary`), `ExceptionHandlers/FallbackProblemDetailsWriter.cs`.
- ~200 LOC po to, by klient, który *nie* wyśle vendorowego Accept, dostał reprezentację bez śladu
  HATEOAS. Tymczasem produkt ma dokładnie dwóch klientów API: frontend **bezwarunkowo** dokleja
  vendorowy Accept na każdym requeście (`TranslationContentNegotiationAndAuthDelegatingHandler.cs:36`
  + bliźniak auth), a CLI dotyka wyłącznie `text/plain` distribution i forum. Gałąź plain-JSON,
  tie-breaking po q-factorach i tłumienie pustych `links` wykonują się tylko dla curla i testów.
  Mechanizm jest częściowo nośny (ścieżka HATEOAS, której frontend używa, przechodzi przez ten sam
  kod) — findingiem jest połowa „czystego plain-JSON".
- *Uproszczenie:* serwować linki bezwarunkowo (wyciąć negocjator + modifier + `Vary`), **albo**
  jednolinijkowy ADR, że opt-in negocjacja to utrzymana decyzja produktowa (np. wizytówkowo-„enterprise").

### Utilities / wspólne

#### W1 — `TimedOperation`: cały feature martwy **[wysoka]**
- `Utilities/LotroKoniecDev.Logging/TimedOperations/TimedOperation.cs` (~130 LOC, trzy arności,
  eskalacja slow-threshold, dwa stłumione warningi analizatora) + `TimedOperationExtensions.cs`.
- Zweryfikowane: hity repo-wide tylko w dwóch plikach definiujących. Zero call-site'ów w trzech
  API, frontendzie, patcherze; zero testów. Projekt Logging jest w dokumentacji przypięty tylko do
  redakcji danych wrażliwych (`SensitiveDataRedactor` — żywy, 3 konsumentów). Klasyczny osad liftu.
- *Uproszczenie:* usunąć folder `TimedOperations`.

#### W2 — `OpenIddictSettings.InternalIssuer`/`EffectiveInternalIssuer`: knob nigdy nie czytany **[wysoka]**
- `AuthSystem.API/Settings/OpenIddictSettings.cs:14,31`.
- Zweryfikowane: jedyne hity repo-wide (kod, compose'y, `.env*`, runbook) to definicja. Override
  „self-referencing calls in Docker" — a auth-serwer nigdy sam siebie nie woła (pipeline
  client-credentials z TKS ADR-0003 celowo nie został przeniesiony, por. ADR-0032).
- *Uproszczenie:* usunąć obie składowe.

### Infra / CI / skrypty

#### I1 — Knoby `Bootstrap__*` pod feature, który nigdy nie powstał **[wysoka]**
- `compose.prod.yaml:151-152` (`Bootstrap__Enabled`, `Bootstrap__PolishTextPath`) + mount
  `./translations:ro` (`:154`) istniejący tylko dla tej ścieżki; `compose.hetzner.yaml:114`;
  `docs/deployment/runbook.md:161-162` (wiersze matrycy env-varów).
- Zweryfikowane: **żaden kod nie czyta** sekcji `Bootstrap` (jedyne hity w src to Serilogowe
  `CreateBootstrapLogger()`); w historii gita nigdy nie istniał plik seedera. To ticket #28
  (M2-17, spec 0001 §Bootstrap), który nie wylądował — a M2 jest DONE i prod żyje, więc seed
  poszedł inną drogą. Env-vary + mount + dokumentacja instalują nieistniejący feature w dwóch
  prod-stackach.
- *Uproszczenie:* usunąć 2 env-vary + mount z obu compose'ów i 2 wiersze runbooka; jeśli #28
  kiedyś wróci, config przyjdzie razem z kodem.

#### I2 — `Dockerfile.tests`: bez wołającego i przegniły **[wysoka]**
- `Dockerfile.tests` (całość; też wpis w `LotroKoniecDev.slnx:19`).
- Zweryfikowane: nic go nie buduje ani nie uruchamia (workflowy, skrypty, compose'y, docs —
  jedyne wzmianki to mapa liftu w CLAUDE.md, notka ADR-0002 i **skip** w guardzie restore-graph
  `scripts/check-dockerfile-restore-graph.sh:22`). CI odpala testy natywnie; E2E przez
  Testcontainers. Zgnilizna dowodzi nieużywania: uruchamia 3 z 11 projektów testowych — ręczny
  run wypisałby fałszywe „ALL TESTS PASSED".
- *Uproszczenie:* usunąć plik + wpis slnx + wzmianki w guardach/CLAUDE.md (mapa liftu — wiersz do
  korekty).

#### I3 — `smoke.yml`: interfejs `workflow_call` „for future callers" z zerem wołających **[wysoka, waga niska]**
- `.github/workflows/smoke.yml:16-38` (4 typowane inputy + blok secrets); własny komentarz
  nagłówka (`:4-7`) przyznaje, że jedyny realny konsument (rollout CD) **celowo** woła
  `scripts/smoke.sh` inline.
- Zweryfikowane: jedyne `uses: ./.github/workflows/…` w repo to cd.yml → deploy.yml. Dosłowna
  YAGNI-autodeklaracja w komentarzu.
- *Uproszczenie:* wyciąć blok `workflow_call` (zostawić `workflow_dispatch`).

#### I4 — Ścieżka release'owa `v*` w cd.yml nigdy nie wykonana **[średnia — przypięta na papierze]**
- `.github/workflows/cd.yml:71-72` (trigger tagów), `:146-148` (pass-through), `:244-245`
  (wzorce semver).
- `git tag` w repo jest **pusty** (sam `n1-compat.yml:8` to przyznaje); deploye używają wyłącznie
  `sha-<short>`; żaden proces nie mintuje tagów. ADR-0012 §3 decyduje „tag `v*` publikuje artefakty,
  nie deployuje" — więc jest zapis decyzji, ale z ery ACA; ścieżka przeżyła przepisanie ADR-0034
  nietknięta i nieużyta. Dlatego średnia, nie wysoka.
- *Uproszczenie:* wyciąć trigger + wzorce semver do czasu realnego procesu wersjonowanych
  release'ów (jednolinijkowa notka przy ADR-0012).

#### I5 — Blok `AUTH_ADMIN_*` w `.env.example` martwy w stacku, który go czyta **[wysoka, trywialne]**
- `.env.example:16-21` („seeded into the auth DB on first boot").
- Zweryfikowane: dev `.env` zasila wyłącznie `compose.yaml`, a ten od #190/M6-14 jest infra-only —
  **nie zawiera żadnej referencji `AUTH_ADMIN`** (host-Kestrele nie czytają `.env`; dev-seed
  przychodzi z `appsettings.Development.json:30`). Bliźniaki `.env.prod.example`/`.env.hetzner.example`
  SĄ konsumowane — martwy jest tylko szablon dev, relikt ery apek-w-compose.
- *Uproszczenie:* usunąć blok z `.env.example`.

---

## Odrzuceni kandydaci (falsyfikacja skuteczna — nie zgłaszać ponownie bez nowych dowodów)

| Kandydat | Dlaczego to NIE jest finding |
|---|---|
| Maszyneria HATEOAS jako całość (linki na zasobach/kolekcjach, `Rels`) | realnie konsumowana — frontend jest link-driven: gating przycisków approve/upsert/import/delete i paginacja przez `HasLink`/`FindLink` (`Editor.razor:169,265`, `Translations.razor:96-268`, `GameVersions.razor:130,182`, `Account.razor:179`); cel RMM-3 jawnie zamierzony |
| `RequireServiceScope`, scope `service`, klient client-credentials, introspection/revocation | ADR-0032 wprost: „stays dormant"; noga tokenowa ćwiczona przez smoke CD (`SMOKE_CLIENT_SECRET`) |
| `auth/register` + `auth/confirm-email` (JSON) | realni konsumenci cross-suite: `UserFactory.cs:43,91` (integracja auth), `AuthApiClient.cs:29` (E2E TMS) |
| Password grant + testowy klient OAuth | Testing-only, jawny komentarz, potrzeba integracyjna/E2E |
| `DiscoveryCache` (HybridCache) + guardy zatrucia | żywi konsumenci nogi auth; klucze per-stan-auth i no-cache-on-failure adresują konkretne tryby awarii |
| Rejestr DeadSession + cookie SessionExpiryNotice | w pełni okablowany łańcuch (`CookieTokenRefresher:67,255` → `MainLayout.razor:57`); rozwiązuje zaobserwowane okno stale-JWKS |
| Single-impl interfejsy z seamem NSubstitute (`IEmailService`, `IUserSessionRevoker`, `IAccountErasureService`, loadery, `ITranslationExportParser`, `IBulkTranslationInserter`, `ICurrentUserAccessor`, …) | faktycznie stubowane w testach — seam wymagany przez filozofię testową repo |
| `IPatchingService`, `IVersionBaselineService` | realny fan-in (2 konsumentów produkcyjnych / seam CLI↔Application); punkt reużycia M4 |
| Porty patchera (`IDatFileHandler`, `IForumPageFetcher`, `IGameLauncher`, `IFileHasher`, …) | prawdziwe granice (native interop / fs / sieć / procesy), wszystkie mockowane |
| Duplikacja Result/Maybe/Error między Patcherem a SharedKernel | CLAUDE.md: konsolidacja jest opt-in, nie mandatem |
| Debounce + `DebounceWindow`, knoby `ImportSettings`, `CorsSettings` | ADR-0021 / czytane i testowane / ADR-0008 §3 |
| HybridCache w `GetPublicProgress`, near-duplikacja z `GetTranslationStats`, hash-only `HashQuery` | decyzje z referencjami do ticketów (#354, #309, PERF-01/#286) |
| Route `{lang}` + kolumna `Language` przy jednym języku | kontrakt CLAUDE.md/spec-0001; `SupportedLanguages` = udokumentowane single source of truth |
| Stack supply-chain (Trivy ×2, cosign, SLSA/SBOM, weryfikacja atestacji przy deployu) | ciężki jak na solo pre-launch, ale jawnie zdecydowany (audyt 0001 H1/H9, poprawki ADR-0012) i każda noga realnie bramkuje |
| `mutation-test.yml` | działa i bramkuje: `break: 67` czerwieni leg, merge-gate loopa czeka na wszystkie checki |
| e2e.yml Dependabot-only, ci.yml pełny gate na main, poll readiness vs smoke vs health-ping | zamierzone (CI-03/#405; CLAUDE.md „CI-green ⇒ CD"; trzy różne cele argumentowane w plikach) |
| Bliźniaki `.ps1` | mandat CLAUDE.md; spot-check 5 par — bez dryfu |
| Resztki Azure | brak funkcjonalnych; `docs/deployment/azure-graveyard/` celowo „entombed" (#505) |
| `Hateoas.Abstractions` jako osobny projekt | realna granica: `Contracts` niesie `LinkDto` bez zależności ASP.NET Core |
| `Options` utility, `SensitiveDataRedactor`, `OpenIddictPruneService`, `SmtpHealthCheck`, knoby `GdprSettings` | zweryfikowani konsumenci (7 / 3+6 / akumulacja refresh-tokenów / deep health / oba propy czytane) |
| `ValueObject` base z jednym pochodnym (`Error`) | równość `Error.None` w ctorze `Result` na nim polega; lifted building block, ~40 linii |
| `CliExitCode` w Tests.E2E duplikujący `ExitCodes` | celowa duplikacja black-box (testy nie sprzęgają się z internalami CLI) |
| Sieć `tks` + `TKS_DOMAIN_*` w compose.hetzner | realny co-hosting stacka TheKittySaver (#506/#507) |

Obserwacja przekrojowa (staleness, nie overengineering): `DefaultTmsBaseUrl = ""` w CLI ma
udokumentowaną rację (AUDIT-SEC-01), ale po M6 (TMS zdeployowany) placeholder jest już zapewne do
podmiany na produkcyjny URL — do rozstrzygnięcia przy M4.

---

## Proponowana kolejność sprzątania (gdyby właściciel chciał domknąć)

1. **Czyste delecje bez ryzyka** (jeden PR „dead code sweep TMS/Auth/Frontend/Utilities"):
   T2, T3, T5, T6, W1, W2, A4 + I3, I5 (infra-trywialne).
2. **Patcher sweep** (osobny mały PR, testy giną razem z kodem): P1, P4, P7, P8, P10; P5 przez
   `dat-format-expert`; P2/P3/P9 w drugiej kolejności (dotykają shape'ów publicznych).
3. **Decyzje właścicielskie przed kodem:** P6 (fail-fast czy wyciąć?), T1+A1+A2+A3 (zwężenie
   powierzchni API — wyrównać `docs/API.md`), A5 (ADR albo wycinka), T4 (dopisać do dormancji
   ADR-0032 albo egzekwować), I1/I2/I4 (kosmetyka infra + korekta mapy liftu w CLAUDE.md).

Żaden finding nie dotyka schematu bazy — nic tu nie podlega dyscyplinie migracyjnej ADR-0023.
