# Audyt infrastruktury #0001 — CI · CD · IaC · środowiska · sekrety · observability

> **Enterprise-grade audyt procesów infrastrukturalnych** całego stacku: GitHub Actions (CI/CD),
> Terraform (IaC, Azure), strategia środowisk, zarządzanie sekretami (Key Vault), baza (Neon),
> observability i runtime-security trzech aplikacji. Cel: ocena dojrzałości **oraz** gotowości do
> zduplikowania proda w izolowane środowisko **staging** (następny krok właściciela).
>
> **Metoda.** 5 niezależnych deep-dive'ów (po jednym na wymiar) + warstwa platformowa GitHub/Azure
> czytana przez API. Każdy finding ma dowód `plik:linia`. Kluczowe tezy zweryfikowane bezpośrednio.
> Wszystkie ścieżki/linie odnoszą się do drzewa repo na dzień audytu.

**Status:** Informacyjny (raport, nie decyzja — decyzje wynikłe trafią do ADR)
**Data:** 2026-06-29
**Zakres:** `.github/workflows/*` · `iac/*` · `compose*.yaml` · `Dockerfile*` · `scripts/*` · `docs/adr/{0005,0008,0012,0013,0014}` · `docs/deployment/*` · bootstrap `Program.cs` (auth/tms/frontend)
**Powiązane:** ADR-0008 (deployment cloud-agnostic + strategia env), ADR-0012 (CD pipeline), ADR-0013 (Key Vault = źródło prawdy), ADR-0014 (DB na Neon), ADR-0005 (persystencja kluczy Data Protection), `docs/deployment/runbook.md` (matryca env-var)

---

## Werdykt

Projekt ma **ponadprzeciętnie przemyślany szkielet jak na solowy pre-release**: passwordless OIDC do
Azure, Key Vault jako realne źródło prawdy (zero plaintextu w stanie TF), mocny PR-gate (build + unit
+ integracja na realnym Postgresie + SSR-purity + secret-scan), brama migracji-przed-rolloutem,
immutable obrazy po SHA, dojrzała auth aplikacji (pinned JWT, auth-code+PKCE, rotacyjne
refresh-tokeny, rate-limiting). **Ale trzy rzeczy podcinają tę dojrzałość:** w chmurze **nie ma
alertów ani telemetrii** (prod jest ślepy operacyjnie), **testy nie są bramką deployu** (CI leci
równolegle do CD), a **`pr-verify` nie jest wymaganym statusem na `main`** (ruleset chroni branch —
push i force-push zablokowane — ale nie bramkuje budowania/testów; patrz korekta C1). Bezpieczny dla jednoosobowego test-proda;
**niegotowy, by zostawić go bez nadzoru ani wpuścić realnych użytkowników**. Duplikacja na staging
jest blisko — pod warunkiem najpierw sparametryzowania IaC i twardej izolacji sekretów/kluczy
per-środowisko.

### Scorecard dojrzałości

| Wymiar | Ocena | Jednym zdaniem |
|---|---|---|
| Runtime security (app) | **4.0 / 5** | Bardzo dobra; drobne luki: nagłówki bezpieczeństwa, zaufanie do proxy. |
| CI · quality gates | **3.5 / 5** | Mocny rdzeń korektności, cienka warstwa security-scanningu. |
| Środowiska · sekrety | **3.5 / 5** | Dojrzałe sekrety i KV; realnie istnieje tylko dev+prod. |
| CD · release | **3.0 / 5** | Czytelny skeleton (OIDC, migracje, smoke), luki w bezpieczeństwie wydania. |
| IaC · Terraform | **3.0 / 5** | Bezpieczny stan i sekrety; płaski, jedno-środowiskowy, hardkody `prod`. |
| **Observability** | **1.5 / 5** | Logi trafiają do LAW, ale zero metryk, traces i alertów w chmurze. |

### Bilans findingów

| Severity | Liczba |
|---|---|
| CRITICAL | 5 (C1 skorygowany po weryfikacji → w większości false positive; realnie 4) |
| HIGH | 14 |
| MEDIUM | 16 |
| LOW | 9 |
| Mocne strony (GOOD) | 22 |

### Korekta wstępnego ustalenia

> **✓ Wycofany finding.** Mój pierwszy alert „lokalny `iac/.terraform/terraform.tfstate` cache'uje
> `access_key` + `client_secret` (plaintext na dysku)" był **fałszywym trafieniem** — grep złapał
> tylko *nazwy* kluczy. Weryfikacja pliku (`:8,11`) pokazała oba pola jako `"access_key": null`,
> `"client_secret": null`. Backend `azurerm` uwierzytelnia się przez OIDC federation (`infra.yml`
> `azure/login@v2`), żaden klucz storage nie materializuje się na dysku. **Obsługa stanu jest
> wzorcowa** — patrz mocne strony G20.

---

## 1. Findingi krytyczne (CRITICAL)

### C1 — [SKORYGOWANE po weryfikacji] `main` jest chroniony **rulesetem**; realny residual: `pr-verify` nie jest wymaganym statusem
- **Obszar:** Governance / GitHub
- **✓ Korekta (zweryfikowane `gh api repos/.../rules/branches/main`, 2026-06-29):** pierwotny finding („`main` całkowicie niechroniony, force-push dozwolony, każdy z write pushuje prosto na `main`") był **w większości false positive** — audyt odpytał *legacy* endpoint `branches/main/protection` (stąd 404), podczas gdy ochrona jest realizowana przez nowszy **repository ruleset** (id `12105984`, `enforcement: active`). Efektywne reguły na `main`: `pull_request` **wymagany** (bezpośredni push **zablokowany**), `non_fast_forward` (force-push **zablokowany**), `deletion` (zakaz usuwania), `required_linear_history` (już włączone — audyt to jedynie *rekomendował*), `required_status_checks` ze `strict: true`.
- **Realny residual (to faktycznie zostaje):** lista wymaganych statusów to wyłącznie `gitleaks` + `GitGuardian Security Checks` — **`pr-verify` (Release-build + unit + integracja) NIE jest wymaganym statusem**, więc PR z czerwonym `pr-verify` da się zmergować. Brak wymaganego review (`required_approving_review_count: 0`) jest świadomy i poprawny dla solo-dev (nie sposób zatwierdzić własnego PR-a).
- **Wpływ:** znacznie niższy niż pierwotnie oceniono. Bezpośredni push na `main` jest zablokowany, force-push też; jedyna luka to brak twardej bramki `pr-verify` przy merge — splata się z C2 (testy nie blokują wydania) i to z C2 płynie faktyczne ryzyko „nietestowany kod na prodzie".
- **Zalecenie:** dodaj `pr-verify` (i ewentualnie `CI`) do `required_status_checks` w rulesecie `main` (`gh api --method PUT .../rulesets/12105984`) — domyka to także governance-owy wymiar C2. Reszta pierwotnej rekomendacji (PR wymagany, zakaz force-push, linear history) jest **już spełniona** rulesetem.

### C2 — Testy NIE blokują deployu; CI leci równolegle do CD
- **Obszar:** CD / pipeline
- **Dowód (zweryfikowane bezpośrednio):** graf zależności `cd.yml` to `build-and-push → deploy-prod (needs: build-and-push) → smoke (needs: deploy-prod)`. Zero `needs`/`workflow_run`/`dotnet test` linkujących do `ci.yml`; `ci.yml` ma własny trigger `on: push: main`.
- **Wpływ:** pełny zestaw Release-build + unit + integracja (`ci.yml`) leci *równolegle* z budową obrazów i rolloutem, nie *przed* nim. Czerwone CI nie blokuje ani `build-and-push`, ani (approval-gated) rolloutu. Approver zatwierdzający wydanie nie ma żadnej gwarancji, że testy przeszły.
- **Zalecenie:** uzależnij `deploy-prod` od sukcesu CI — albo wciągnij job testowy do `cd.yml` jako `needs:`, albo zbramkuj deploy przez `workflow_run` CI = success. Minimum: pokaż status CI w decyzji approvala.

### C3 — Zero alertów: prod jest ślepy operacyjnie
- **Obszar:** Observability
- **Dowód:** w `iac/` brak jakiegokolwiek `azurerm_monitor_*` / `scheduled_query_rules` / `metric_alert` / `action_group` (grep = nic). Brak alertu na error-rate, 5xx, latencję, restart/crash-loop repliki, łączność z DB, wygaśnięcie certu, daily-cap LAW, dostępność Key Vault.
- **Wpływ:** w połączeniu z brakiem metryk/traces (H3) produkcja jest *operacyjnie niewidoma* — awarię wykrywa dopiero właściciel albo użytkownik. Dla serwera auth (wydawanie tokenów = SPOF całej platformy) to najwyższy operacyjny gap.
- **Zalecenie (minimum, w całości Terraform-owalne):** `azurerm_monitor_action_group` (e-mail/SMS) + alerty: (1) restart/unhealthy-revision Container App, (2) `scheduled_query_rules_alert_v2` na spike `level=Error`/`Fatal` w `ContainerAppConsoleLogs_CL`, (3) daily-cap LAW, (4) dostępność `/health/ready` z zewnątrz. Zdefiniuj choć jedno SLO (dostępność auth) jako cel alertu.

### C4 — Migracje forward-only na jedynej żywej bazie Neon, bez snapshotu i rollbacku
- **Obszar:** CD / baza danych
- **Dowód:** ADR-0012 §4 „Forward-only migrations (no automated rollback)"; `apply-migrations.sh:19` `set -e` przerywa, ale nie cofa; `migrator-job.tf:8` `replica_retry_limit = 1`; w `cd.yml` (linie 174-200) brak kroku snapshotu DB przed migratorem.
- **Wpływ:** brama migracji leci na *jedyną żywą bazę* bez automatycznego snapshotu. Migracja zła-ale-udana (drop kolumny, zwężenie typu) jest nieodwracalna bez ręcznego Neon PITR. Ironia: Neon ma branching (CoW), który dałby snapshot za grosze — ale jest nieużywany (H13).
- **Zalecenie:** dodaj automatyczny pre-migration branch/snapshot jako krok pipeline'u (gałąź Neon tuż przed `az containerapp job start`). Udokumentuj ścieżkę restore w runbooku. Docelowo: dyscyplina expand-contract + staging do wyłapywania destrukcyjnego DDL *zanim* dotknie proda.

### C5 — [Staging-blocker] Naiwny staging wystartuje na PRODOWYCH sekretach i może współdzielić klucz podpisujący
- **Obszar:** Staging / bezpieczeństwo
- **Dowód:** `iac/vars.tf:27` `key_vault_name default = "lotrotms-kv-prod"`; sekrety w KV nie mają prefiksu env (`connection-string-*`, `openiddict-signing-key`, `admin-password`). `terraform apply -var env_id=staging` bez nadpisania vaulta każe apkom staging czytać *prodowy* vault.
- **Wpływ:** początkujący QA dostałby środowisko uwierzytelniające się *prodowym* connection-stringiem i prodowym hasłem admina. Współdzielony klucz podpisujący OpenIddict = token wykuty na staging waliduje się na prodzie (*cross-env forgery*). To najwyższe ryzyko bezpieczeństwa następnego kroku.
- **Zalecenie:** twarda izolacja per-env — osobny Key Vault (`lotrotms-kv-staging`), świeżo wygenerowane sekrety (`gen-openiddict-keys.sh` + `openssl rand` dla admina), osobna gałąź Neon. Nigdy nie współdziel prodowego vaulta ze środowiskiem dostępnym dla nowicjusza. Szczegóły w sekcji 6 (blueprint).

---

## 2. Findingi wysokie (HIGH)

### H1 — Cała warstwa security-scanningu nieobecna: brak SAST, audytu zależności i skanu obrazów
- **Obszar:** CI/CD / supply chain
- **Dowód:** brak CodeQL/SARIF (grep = nic), brak `NuGetAudit` / `dotnet list package --vulnerable`, brak Dependabot/Renovate (`dependabot_security_updates: disabled` na repo), brak skanu obrazów (Trivy/Grype/Scout) — a `cd.yml:99` wypycha 4 obrazy na GHCR→ACA.
- **Wpływ:** CVE w OpenIddict/Npgsql/MailKit ani podatna warstwa bazowa nigdy nie wypłyną. Dla serwera auth + 4 obrazów to największy gap „enterprise". Obecne ryzyko łagodzi świeżość zależności (.NET `10.0.5`), więc to przede wszystkim o *pozostawaniu* świeżym. *(Agent CI ocenił to jako CRITICAL.)*
- **Zalecenie:** krok `dotnet list package --vulnerable --include-transitive` (fail ≥Moderate) w `pr-verify`+`ci`; `<NuGetAuditMode>all</NuGetAuditMode>` w `Directory.Build.props`; CodeQL (csharp) na PR + tygodniowo; Trivy na obrazy w `cd.yml` (fail HIGH/CRITICAL); Dependabot dla `nuget`+`github-actions`+`docker`.

### H2 — [Staging] IaC nie jest multi-env: nazwa RG i ~10 URL-i prod zahardkodowane
- **Obszar:** Staging / IaC
- **Dowód:** `resource-group.tf:3` `name = "rg-lotrotms-prod-polc-001"` (literał, referowany 52×, nie pochodna `env_id`). `azure-container-apps.tf` ma **10 literałów** `lotro-translator.pl` (linie 113/129/133/137/297/301/309/402/406/414): issuer, redirect, CORS, authority, base-URL frontendu.
- **Wpływ:** naiwny apply na staging tworzy zasoby *w prodowym RG*, a tokeny niosłyby prodowy `iss` i redirecty → login staging pada (issuer mismatch / invalid redirect / CORS reject).
- **Zalecenie:** przemianuj symbol RG na `main` + `name = "rg-lotrotms-${var.env_id}-polc-001"` (przez `moved {}`, nigdy destroy/recreate); wprowadź `var.public_base_domain` i wyprowadź wszystkie URL-e przez `locals`.

### H3 — Pipeline OTel zbudowany, ale w chmurze podłączony do niczego
- **Obszar:** Observability
- **Dowód:** `Program.cs:39,110-113` (wszystkie 3 apki) konfigurują traces+metryki (ASP.NET, HttpClient, Runtime, EF, Npgsql), ale eksporter włącza się tylko `if (!IsNullOrWhiteSpace(otlpEndpoint))` — a *żaden* `OTEL_EXPORTER_OTLP_ENDPOINT` nie jest ustawiony na żadnym Container App (jest tylko w `compose.prod.yaml` za profilem `local-otel`).
- **Wpływ:** w produkcji *zero* distributed traces i *zero* metryk — brak percentyli latencji, map zależności, sygnału request/error-rate. aspire-dashboard jest dev-only. Cały prod leci na samych logach.
- **Zalecenie:** wystaw cel OTLP (Azure Monitor / Application Insights Distro — najmniejsze tarcie, skoro LAW już jest) i wstrzyknij `OTEL_EXPORTER_OTLP_ENDPOINT` per Container App.

### H4 — [Staging] Brak izolacji keyringu Data Protection + nazw sekretów per-środowisko
- **Obszar:** Staging / security
- **Dowód:** `SetApplicationName("LotroKoniecDev.AuthSystem")` to stała kompilacji; sekrety KV bez prefiksu env. Share AzureFile jest per-env *tylko* przez `env_id`.
- **Wpływ:** dziś jedno env — brak szkody. Przy staging: współdzielony klucz podpisujący/keyring = cross-env forgery + wyciek prodowych credów. Wymaga, by staging realnie miał inny `env_id`.
- **Zalecenie (najważniejszy item przed staging):** osobny Key Vault per env (najczystsza granica blast-radius), prefiksy `prod-`/`staging-` na sekretach jeśli wspólny vault, potwierdź odrębny `env_id` → odrębny share keyringu.

### H5 — [Staging] Płaski root module + jeden monolityczny klucz stanu — drugiego env nie da się zainstancjonować
- **Obszar:** IaC / struktura
- **Dowód:** 9 plików `.tf` na top-levelu `iac/`, brak `modules/` i `module {}`. `setup.tf:13` `key = "prod.terraform.tfstate"` (stały — backend nie może użyć zmiennej).
- **Wpływ:** by dodać staging nie da się niczego zainstancjonować — trzeba albo duplikować katalog, albo polegać na jednym zestawie zmiennych przesyconym literałami `prod`. Strukturalny korzeń problemu duplikacji.
- **Zalecenie:** wyłuskaj `modules/environment/` przyjmujący `env_id`+`base_domain`; root = cienkie `module "prod"` / `module "staging"`. Klucz stanu per-env przez `-backend-config`. Ranking podejść w sekcji 6.

### H6 — Akcje GitHub i obrazy bazowe na ruchomych tagach, nie SHA/digest
- **Obszar:** CI/CD / supply chain
- **Dowód:** każdy `uses:` to `@vN` (`docker/build-push-action@v7`, `gitleaks-action@v2`, `hashicorp/setup-terraform@v3`, `azure/login@v2`). Wszystkie 5 Dockerfile: `FROM mcr.microsoft.com/dotnet/aspnet:10.0` (ruchomy tag, brak `@sha256`).
- **Wpływ:** ruchomy tag jest re-pointowalny przez wydawcę — w workflow trzymającym `id-token: write` + GHCR `packages: write` + Azure OIDC to klasyczny wektor supply-chain (typu tj-actions). Podważa też „immutable per-commit image" z ADR-0012.
- **Zalecenie:** przypnij akcje third-party do pełnego SHA (`@<sha> # v7.x`) i obrazy bazowe do digestu; Dependabot (`github-actions`+`docker`) utrzyma je świeże.

### H7 — Rollout apek nie jest health-gated i nie ma auto-rollbacku
- **Obszar:** CD / release safety
- **Dowód:** `cd.yml:203-217` `roll()` robi `az containerapp update --image` i wraca; readiness pollowany *tylko* dla auth+tms (`:236`) — **frontend nigdy**; `revision_mode = "Single"` + `traffic latest_revision=true 100`.
- **Wpływ:** 100% ruchu przeskakuje na nową rewizję natychmiast; jeśli jest niezdrowa — brak automatycznego cofnięcia ruchu (nieudany wait wywala *job*, ale ruch już się przeniósł). Smoke leci *po* tym, jak ruch jest żywy.
- **Zalecenie:** multi-revision + label-based traffic shift (deploy na 0%, smoke, potem 100%), albo jawny krok rollback (`az containerapp revision activate <prev>`) na porażkę smoke/readiness. Dodaj readiness frontendu do `wait_ready`.

### H8 — `UseForwardedHeaders` ufa WSZYSTKIM proxy → bypass rate-limitera przez `X-Forwarded-For`
- **Obszar:** Runtime security
- **Dowód:** `AuthSystem/Program.cs:131-138` (i tms/frontend) `KnownIPNetworks.Clear(); KnownProxies.Clear();`. Apka jest internet-facing (`external_enabled = true`). Limiter brute-force partycjonuje po `RemoteIpAddress` (`:206-237`).
- **Wpływ:** spoofowany `X-Forwarded-For` podmienia klucz partycji rate-limitera, pozwalając rotować IP i omijać limity na forgot-password/login. Spoofowany `X-Forwarded-Host` może zatruć URL-e pochodne (reset-linki). *Plus:* issuer OIDC jest pinned (`:107`), więc token `iss` jest odporny — to neutralizuje najgroźniejszy wektor.
- **Zalecenie:** `ForwardLimit = 1`, ogranicz `KnownNetworks` do podsieci ingress ACA, albo wyprowadź klucz rate-limitera z zaufanego źródła. Potwierdź, że base-URL reset-linku też jest pinned.

### H9 — Brak provenance / SBOM / podpisu obrazów
- **Obszar:** CD / supply chain
- **Dowód:** `cd.yml:112` `provenance: false`; brak `sbom:`, attestation, `cosign`.
- **Wpływ:** brak atestacji supply-chain — nie udowodnisz, który commit/builder wyprodukował obraz; brak SBOM do triażu CVE; obrazy niepodpisane. Mocny negatywny sygnał enterprise (SLSA).
- **Zalecenie:** `provenance: true` + `sbom: true` na build-push-action; `cosign` + GitHub artifact attestation; weryfikuj podpis przy deployu.

### H10 — Jedno środowisko: prod jest jedynym celem, brak modelu promocji
- **Obszar:** CD / architektura
- **Dowód:** ADR-0012: „prod = staging", `cd.yml:18` dosłownie „prod is de-facto staging for QA". Każdy push na `main` → kandydat na prod; brak joba/env staging.
- **Wpływ:** rollout, migracja i smoke dzieją się pierwszy-i-jedyny raz na żywym prodzie. Approval kontroluje *kiedy*, nie *czy bezpiecznie*.
- **Zalecenie:** model promocji — merge→auto-deploy *staging* (bez approvala), potem gated promocja *tego samego* immutable obrazu `sha-<short>` na prod. Obrazy są już immutable, więc promocja-po-tagu jest gotowa.

### H11 — Brak `prevent_destroy` na zasobach stanowych (Storage, LAW)
- **Obszar:** IaC / reliability
- **Dowód:** ani `storage.tf`, ani `azure-law.tf` nie mają `lifecycle { prevent_destroy = true }`.
- **Wpływ:** wpadka w planie/stanie (np. źle zrobiony rename RG albo poślizg `-target`) może *zniszczyć* storage z keyringiem Data Protection (wyloguje wszystkich) albo workspace LAW (utrata historii). KV jest data-only, więc TF go nie zniszczy — dobrze — ale storage jest TF-owned i niechroniony.
- **Zalecenie:** `prevent_destroy = true` na `azurerm_storage_account.keys` i `azurerm_log_analytics_workspace.law`. Interaguje z rename RG — rename rób przez `moved {}`/`state mv`.

### H12 — Brak udokumentowanej polityki backupów / PITR dla Neon
- **Obszar:** Środowiska / DB
- **Dowód:** `target-requirements.md:113` „HA / backup topology … is not committed here"; ADR-0014 wspomina backupy 0 razy.
- **Wpływ:** brak stanowiska o backupie/PITR/retencji dla żywej prodowej bazy. Neon free ma ograniczone okno historii (~24h-7d zależnie od planu). Dla prod-z-przyszłymi-userami to gap; dla staging akceptowalne (dane jednorazowe).
- **Zalecenie:** udokumentuj realne okno retencji Neon i — dla proda — zdecyduj kadencję PITR/branch-backup zanim przyjdą użytkownicy.

### H13 — [Staging] Neon branching — najtańszy lewar izolacji — w ogóle nieużywany
- **Obszar:** Środowiska / DB
- **Dowód:** ADR-0014 „One Neon project … two databases"; słowa „branch/branching" jako strategia env nie padają (zweryfikowane gerpem — pada tylko w kontekście gita).
- **Wpływ:** sztandarowa funkcja Neon — copy-on-write branch (niemal-natychmiastowy, niemal-darmowy klon danych/schematu per env) — jest niewykorzystana. Dla staging to najważniejsze przeoczenie: branching to *idiomatyczny*, najtańszy sposób na izolowane dane staging.
- **Zalecenie:** utwórz gałąź Neon `staging` z prodowej. CoW, ~zero kosztu, własny compute endpoint + connection-stringi, izoluje zapisy od proda. Szczegóły w sekcji 6.

### H14 — Brak `global.json` i brak lockfile NuGet — buildy niedeterministyczne
- **Obszar:** CI / reprodukowalność
- **Dowód:** `global.json` ABSENT, `packages.lock.json` ABSENT, brak `RestorePackagesWithLockFile`/`ContinuousIntegrationBuild`. SDK przypięty w workflow, ale nie dla lokalnych devów ani Dockerfile migratora.
- **Wpływ:** dev na innym 10.x SDK buduje/restore'uje inaczej; bez lockfile `dotnet restore` może rozwiązać inne wersje przechodnie niż widziało CI (central management pinuje direct, nie transitive).
- **Zalecenie:** root `global.json` (`10.0.100`, `rollForward: latestPatch`); `RestorePackagesWithLockFile=true`, commit `packages.lock.json`, `--locked-mode` w CI.

---

## 3. Findingi średnie (MEDIUM)

| ID | Obszar | Finding | Dowód | Zalecenie (skrót) |
|---|---|---|---|---|
| M1 | Sekrety | Dualne źródło sekretów — KV „single source of truth" + 8 GitHub Actions secrets (`CONNECTION_STRING_*`, `OPENIDDICT_*`, `SMTP_*`, `ADMIN_PASSWORD`, `SMOKE_CLIENT_SECRET`) | repo secrets | Udokumentuj GH-secrets jako upstream seedujący KV; nie udawaj, że KV jest jedynym źródłem. |
| M2 | CD | CD bez `paths-ignore` — commity docs odpalają deploy (stąd run CD 1h2m czekający na approval; jeden `cancelled`) | `cd.yml` (on: push) | Dodaj `paths-ignore` (md/docs) jak w `ci.yml`/`pr-verify`. |
| M3 | CD/IaC | `min_replicas = 0` przeczy ADR-0012 R8 („lifted 0→1") — drift dok/kod | `azure-container-apps.tf:58,242,351` | Pogódź: jeśli scale-to-zero celowe (koszt) — popraw ADR; jeśli R8 stoi — ustaw 1. |
| M4 | Środowiska | ADR-0008 („staging + production") przeczy ADR-0012 („no staging — YAGNI") | `ADR-0008:14` vs `ADR-0012:27` | Przy budowie staging dopisz/zaktualizuj ADR: „staging istnieje, N env przez `env_id`+`base_domain`". |
| M5 | Observability | Brak redakcji PII/tokenów w logach — `UseSerilogRequestLogging` loguje query-stringi z `code`/`token`/email | `Program.cs` (×3) | Filtr request-logu strip `code/token/password`; `Destructure.ByMaskingProperties` dla email/token. GDPR + token-replay. |
| M6 | Runtime sec | Brak nagłówków bezpieczeństwa na frontendzie (CSP / nosniff / X-Frame / Referrer-Policy) | `Frontend/Program.cs` | Mały middleware nagłówków; min. `nosniff`, `frame-ancestors 'none'`, CSP scoped do własnego + auth origin. |
| M7 | Runtime sec | HSTS na defaultach frameworka (30 dni, bez preload/includeSubDomains) | `UseHsts()` (brak `AddHsts`) | `AddHsts`: `MaxAge=365d`, `IncludeSubDomains`, `Preload` — gdy domena stabilna. |
| M8 | Runtime sec | `Trust Server Certificate=true` z parity-stacku może wyciec do prodowego connection-stringu Neon | `compose.prod.yaml:31` | **Zweryfikuj:** prodowe `connection-string-*` w KV mają `Ssl Mode=Require` *bez* Trust-Cert (Neon ma publiczny CA). Verify-and-confirm, nie udowodniona wada. |
| M9 | IaC/Obs | Daily cap LAW `0.16 GB` → przy error-storm logi cicho znikają (blind-spot w czasie awarii) | `azure-law.tf:7` | Zostaw cap dla kosztu, ale dodaj alert „daily cap reached"; rozważ ~0.5 GB przed staging. |
| M10 | Sekrety | Key Vault purge-protection WYŁĄCZONA (tylko soft-delete) | `ADR-0013:98`, `seed-keyvault.sh` | Włącz `--enable-purge-protection` gdy przyjdą realni użytkownicy (ADR już to zapowiada). |
| M11 | Sekrety | Pojedynczy statyczny klucz podpisujący OpenIddict — brak overlapping-key rollover | `gen-openiddict-keys.sh:22` | OK dla pre-release; post-MVP: wiele kluczy podpisujących dla zero-downtime rotacji. |
| M12 | CD | Concurrency deploy ≠ concurrency infra — `apply` i rollout mogą lecieć równolegle na tych samych apkach | `cd.yml:130` vs `infra.yml:33` | Wspólna grupa concurrency (`prod-mutation`) by serializować apply i rollout. |
| M13 | CD | Budżet pollingu migracji (20 min) < timeout joba (30 min) — mylący stan na wolnym runie | `cd.yml:186` vs `migrator-job.tf:7` | Zrównaj budżet pollingu z `replica_timeout_in_seconds` (≥30 min). |
| M14 | Observability | Brak Key Vault w readiness — niedostępność KV widać dopiero jako twardy boot-fail | health checks (auth/tms) | Lekki check KV tagged `ready` na auth-api, albo alert dostępności KV. |
| M15 | IaC | Brak `required_version` Terraform core (przypięty tylko w CI YAML) | `setup.tf:1-15` | `required_version = "~> 1.15"` w bloku `terraform {}`. |
| M16 | CI | Warnings-as-errors niedeterministyczne — `AnalysisLevel=latest` zmienia zestaw analizatorów z SDK | `Directory.Build.props:4-5` | Przypnij `AnalysisLevel` do pasma (np. `10.0`) i `LangVersion=13` + `global.json`. |

**Dodatkowe MEDIUM (skrót z raportów):** brak gate'u coverage (coverlet obecny, nikt nie zbiera/nie progu); mutation-gate wąski (tylko Domain/Primitives/SharedKernel) i nielimitowany kosztowo (~30 min/run, brak `--since`); `.dockerignore` nie wyklucza `tests/ docs/ iac/ .github/`; `secrets: inherit` w smoke (zbyt szerokie — przekazuje wszystkie sekrety repo); brak presetu URL per-env w smoke (free-text input); matryca env-var (`runbook.md:43-146`) nieegzekwowana mechanicznie wobec IaC; `.env.prod.example:16` niesie placeholder `changeme-prod-password` (przechodzi walidację jeśli niezmieniony); `format --verify-no-changes` nie jest osobnym gate'em (tylko `EnforceCodeStyleInBuild`); Supabase wciąż żyje (paused), oczekuje teardownu operatora (Phase 6, `neon-migration-plan.md:263`); brak zewnętrznego/syntetycznego probe'u uptime.

---

## 4. Findingi niskie (LOW)

| ID | Finding |
|---|---|
| L1 | Redundantny build CI vs pr-verify (świadomy backstop dla direct-push — defensywne, ale na squash-self-merge to niemal czysta duplikacja compute). |
| L2 | Klucz cache NuGet może podać stary restore (brak lockfile → bump transitive nie busta cache). |
| L3 | Brak rekordu deploymentu / changelogu / tagu prod na rollout — trudno odpowiedzieć „co jest teraz na prodzie?". |
| L4 | Storage redundancy `LRS` (single-DC) dla keyringów DP — utrata = re-login userów; akceptowalne (to nie dane biznesowe — te są w Neon). |
| L5 | Brak custom scale rules (przy `max_replicas=1` skalowanie i tak wyłączone). |
| L6 | Access-tokeny niezaszyfrowane (`DisableAccessTokenEncryption`) — celowe i standardowe (są podpisane RS256 ≥2048). |
| L7 | Host prodowej bazy Neon (endpoint) jest w trackowanym docu (`neon-migration-plan.md:34`) — hasło to placeholder; sam host nie jest exploitowalny. |
| L8 | Cookie frontendu `SecurePolicy=SameAsRequest` (bezpieczne tylko bo forwarded-headers ustawia `https`; `Always` byłoby pewniejsze). |
| L9 | Brak progu złożoności hasła seed-admina; staging publiczny + słabe hasło = miękki cel (generuj `openssl rand`). |

---

## 5. Mocne strony (GOOD) — zachować przy duplikacji

| ID | Mocna strona |
|---|---|
| G1 | **Passwordless OIDC do Azure** (`id-token` + `azure/login`, zero `AZURE_CREDENTIALS`) — top sygnał enterprise. |
| G2 | **Key Vault jako realne źródło prawdy** — TF data-references versionless URI, nigdy nie czyta `.value` → zero plaintextu w stanie. |
| G3 | **Rotacja sekretów decoupled od deployów** (versionless URI) — udowodnione migracją Neon. |
| G4 | **User-assigned MI z wąskim `Key Vault Secrets User`**; RBAC poza TF, CI nigdy nie potrzebuje RBAC-admin. |
| G5 | **Environment `production` z wymaganym reviewerem** — ręczna bramka przed prodem. |
| G6 | **Secret scanning + push protection ENABLED** na repo (stąd brak wycieków). |
| G7 | **Brama migracji-przed-rolloutem** — apki nigdy nie wjeżdżają na niezmigrowany schemat. |
| G8 | **Immutable obrazy po `sha-<short>`** + `lifecycle.ignore_changes` na image (TF=kształt, CD=wersja). |
| G9 | **Mocny PR-gate**: SSR-purity → Release-build (warnings-as-errors) → unit → integracja na *realnym* Postgresie + secret-scan, *przed* merge. |
| G10 | **Guardy przeciw false-green** — `exit 1` gdy zero projektów testowych zmatchowanych. |
| G11 | **Higiena concurrency** — testy cancel-in-progress, deploye/apply nigdy nie przerywane (`cancel-in-progress: false`). |
| G12 | **Smoke post-deploy** (health + token round-trip + token-accepted-by-tms + ETag/304). |
| G13 | **Aplikacja env-name-agnostycznie zahartowana** — guardy na `IsDevelopment()/IsTesting()`, nie `=="Production"`; `Staging` dziedziczy pełen reżim bez zmiany kodu (i jest unit-testowany). |
| G14 | **JWT validation w pełni pinned** (issuer/audience/lifetime/signing-key, `MapInboundClaims=false`, `RequireHttpsMetadata`). |
| G15 | **OIDC issuer pinned** (nie scheme-derived) — token `iss` odporny na host-injection. |
| G16 | **CORS allowlist + fail-fast** (`ValidateOnStart`), nigdy wildcard-with-credentials. |
| G17 | **Auth-code + PKCE end-to-end** + rotacyjne, odwoływalne reference refresh-tokeny. |
| G18 | **Rate-limiting warstwowy** na auth (20/min globalnie, 10/min `/connect/*`, 3/15min forgot-password). |
| G19 | **Keyring DP persisted + pinned app-name + fail-fast guard** (głośno-zamiast-cicho). |
| G20 | **Provider Terraform exact-pinned (`4.7.0`) + tracked lockfile**; stan zdalny z auto-lockiem (blob lease); backend bez klucza na dysku (OIDC). |
| G21 | **Plan-on-PR drift gate** (`infra.yml` na `iac/**`) + `fmt -check`. |
| G22 | **Liveness/readiness split poprawny** + SMTP celowo poza readiness; Dockerfile multi-stage, non-root, healthcheck. |

---

## 6. Blueprint środowiska staging

Twój następny krok. Dobra wiadomość: **aplikacja jest już staging-ready** (G13) — robota jest w IaC,
sekretach i bazie, **nie w kodzie**. Najtańsza izolowana ścieżka na tym dokładnie stacku:

### Baza — gałąź Neon, nie nowy projekt
Branch `staging` z prodowej gałęzi w tym samym projekcie. CoW (≈darmowy, natychmiastowy), dziedziczy
obie bazy (`lotro_translation` + `lotro_auth`) + schematy, daje *własny* compute endpoint → własne 2
connection-stringi. Zostaw **direct** (non-pooler) host (migracje EF) + `Ssl Mode=Require`. Osobny
projekt = przesada; wspólna baza = łamie udokumentowaną parność dwóch baz.

### Sekrety — osobny Key Vault, nigdy współdzielony
`KV_NAME=lotrotms-kv-staging scripts/seed-keyvault.sh` ze *świeżymi* wartościami (nowy
`gen-openiddict-keys.sh`, connection-stringi gałęzi staging, `openssl rand` dla admina). Przekaż
`TF_VAR_key_vault_name=lotrotms-kv-staging`. KV-per-env jest **obowiązkowe**, bo env jest dostępny
dla nowicjusza-QA (zob. C5, H4).

### Data Protection — keyring izolowany automatycznie (zweryfikuj)
`storage.tf` nazywa share `lotrotmskeys${var.env_id}`, więc apply z `env_id=staging` dostaje własny
storage + share (keyringi izolowane za darmo). `SetApplicationName` jest env-niezależny — to OK, bo
klucze nie krzyżują się przez różny *storage*. **Warunek:** staging musi realnie mieć inny `env_id`.

### IaC — ranking podejść dla TEGO repo
1. **Rank 1 (zalecane): `modules/environment/`** — wyłuskaj 9 plików do modułu (`env_id`+`base_domain`+sizing); root = `module "prod"` / `module "staging"`. Leczy płaski-root + hardkody w jednym refaktorze. ~1.5-2 dni.
2. **Rank 2 (najszybsze, realna izolacja stanu):** jeden root + per-env `*.tfvars` + `terraform init -backend-config="key=staging.terraform.tfstate"` (osobne pliki stanu). Botched staging apply nie tknie stanu proda. ~0.5-1 dzień.
3. **Rank 3 (workspaces):** słabsza izolacja stanu (co-located) + hazard złego workspace; i tak wymaga całej parametryzacji. Niżej.
4. **Rank 4 (copy-paste katalogu):** odradzane — natychmiastowy drift, każda zmiana 2× ręcznie.

### 6 URL-i OIDC/CORS które MUSZĄ się zmienić — jedyna realna edycja IaC
1. auth `OpenIddict__Issuer` → `https://auth.staging.<domena>` (to token `iss`)
2. auth `RedirectUris__0` → `https://staging.<domena>/callback` · `PostLogoutRedirectUris__0` → `https://staging.<domena>`
3. auth + tms `Cors__AllowedOrigins__0` → `https://staging.<domena>`
4. tms `Auth__Issuer` **i** `Auth__Authority` → bajtowo-identyczne z issuerem auth
5. frontend `AuthSystem__Authority` / `AuthSystem__BaseUrl` / `TranslationSystem__BaseUrl` → originy staging
6. Reszta (`ASPNETCORE_ENVIRONMENT=Production` by guardy odpaliły, `DataProtection__KeyRingPath=/keys`, brama migratora, obrazy GHCR) — **reużyta dosłownie**. Staging dzieli prodowy *obraz*, różni się tylko env-varami + gałęzią DB + vaultem.

### Top 3 ryzyka naiwnej duplikacji prod→staging
1. **Staging na prodowych sekretach** — default `key_vault_name=lotrotms-kv-prod` daje apkom staging prodowy connection-string + hasło admina. → osobny vault, obowiązkowo.
2. **Zepsuty login staging** — zahardkodowane literały `lotro-translator.pl` dają tokeny z prodowym `iss` i 401/invalid-redirect. → parametryzacja 6 URL-i wyżej.
3. **Przypadkowe sprzężenie z danymi proda** — branching jest bezpieczny (CoW, izolowane zapisy), ale skopiowanie prodowego stringu „dla oszczędności czasu" albo wskazanie staging na prodową gałąź = pomyłkowe DDL nowicjusza uderza w prod. → dedykowana gałąź `staging`, zweryfikowana zanim apki ją dostaną.

### Blast-radius nowicjusza-QA
Przy poprawnej izolacji per-env QA w staging jest *kontenerowany* — może zepsuć dane staging,
zablokować admina staging, zaspamować SMTP staging; nic z tego nie tyka proda. Dane: dziś prod ma
*zero* użytkowników (jeden seedowany admin), więc PII-blast jest ~zerowy. To czyni *poprawnie
izolowany* staging dla początkującego genuinie niskim ryzykiem — ale **izolacja sekretów/kluczy (C5,
H4) jest warunkiem bramkowym, zanim QA cokolwiek dotknie.** Dwie poboczne uwagi: staging NIE może
lecieć z parity-owym `Trust Server Certificate=true` (M8), i musi działać jako `Production`, nie
`Testing` (inaczej `AllowPasswordFlow`/`DisableTransportSecurityRequirement` osłabia auth).

### Sekwencja (ważne)
Rename RG i `prevent_destroy` (H11) interagują — dodaj `prevent_destroy`, wykonaj rename przez
`moved {}`/`state mv` (nigdy destroy/recreate), zweryfikuj no-op `plan` na prodzie **przed**
wprowadzeniem modułu/tfvars staging. Out-of-band `seed-keyvault.sh` dla staging musi poprzedzić
pierwszy `terraform apply` (KV + identity to data sources, nie zarządzane zasoby).

---

## 7. Mapa napraw — priorytetyzowana

### Przed staging (blokery)
1. **Osobny Key Vault + świeże sekrety** per-env (C5) — nigdy prodowy vault.
2. **Gałąź Neon `staging`** z własnym endpointem (H13).
3. **Parametryzacja IaC**: nazwa RG przez `moved{}` + `var.public_base_domain` dla 10 URL-i (H2, H5).
4. **Per-env klucz stanu** (`-backend-config`) + odrębny `env_id` (izolacja keyringu DP, H4).
5. **Model promocji**: merge→staging (bez gate), gated promocja tego samego obrazu→prod (H10).

### Przed realnymi użytkownikami (operacyjne)
1. **Alerty Azure Monitor** + action group (C3) — wyjście ze ślepoty.
2. **Eksporter telemetrii w chmurze** (App Insights / OTLP, H3).
3. **Testy bramkują deploy** (`needs`/`workflow_run`, C2).
4. **`pr-verify` jako wymagany status w rulesecie `main`** (C1 — ruleset już chroni branch; brakuje tylko bramki build/test, wspólnej z C2).
5. **Snapshot/branch pre-migracja** + ścieżka restore (C4); `min_replicas=1` dla auth.
6. **Backupy/PITR Neon** udokumentowane (H12); purge-protection KV (M10).

### Hardening enterprise (wiarygodność)
1. **Security-scanning**: `NuGetAudit` + CodeQL + Trivy + Dependabot (H1).
2. **Przypięcie akcji do SHA** + obrazów do digestu (H6).
3. **provenance + SBOM + cosign** (H9); `global.json` + lockfile (H14).
4. **Health-gated rollout** + auto-rollback + readiness frontendu (H7).
5. **`prevent_destroy`** na storage/LAW (H11); `required_version` TF (M15).
6. **Nagłówki bezpieczeństwa** frontendu (M6) + HSTS (M7) + redakcja PII w logach (M5) + fix forwarded-headers (H8).

> **Uwaga sekwencyjna:** „Przed staging" i część „przed userami" (testy-gate, branch-protection)
> warto zrobić *łącznie* z duplikacją — model promocji i tak dotyka `cd.yml`/`infra.yml`, więc kilka
> napraw spina się w jeden refaktor.

---

## Metodyka

Audyt wygenerowany z 5 niezależnych deep-dive'ów (CI · CD · IaC · środowiska/Neon ·
observability/security) działających równolegle, plus warstwy platformowej GitHub/Azure czytanej
przez API (branch protection, Environments, security-features, sekrety/zmienne repo, zdrowie
pipeline'ów). Wszystkie dowody cytowane jako `plik:linia` z drzewa repo na `2026-06-29`. Kluczowe
tezy zweryfikowane bezpośrednio: testy-nie-blokują-deployu (graf `cd.yml`), niechroniony `main`
(404 z API), 10 hardkodów URL (`azure-container-apps.tf`), default KV = prod (`vars.tf:27`), brak
Neon-branchingu (grep ADR-0014 + plan migracji), env-name-agnostyczne guardy (10+ miejsc w 3 apkach).
Jeden wstępny finding (plaintext `access_key`/`client_secret` w tfstate) **wycofany** po weryfikacji
(`.terraform/terraform.tfstate:8,11` → oba `null`; backend na OIDC).
