# Tutorial: Auth w LotroKoniecDev TMS — od zera do rozmowy rekrutacyjnej

> Przewodnik po uwierzytelnianiu i autoryzacji w TMS, oparty na kodzie (`AuthSystem.API`,
> `TranslationSystem.API/Auth`, klient OIDC we `Frontend`). Cel: (1) rozumieć system na tyle, by go
> rozwijać; (2) umieć go opowiedzieć na rozmowie. **Kod jest źródłem prawdy** — przy rozbieżności
> czytaj plik i popraw ten dokument. Lokalizacja to plik; numerów linii nie podajemy, bo szybko się
> dezaktualizują.
>
> Kontrakty HTTP: [API.md §2 + §7](API.md#2-authentication--authorization-model). Reguły:
> [INVARIANTS.md §11](INVARIANTS.md#11-authsystem--uwierzytelnianie). Kto co sprawdza: ADR-0067.

## Spis treści

1. [Słownik na start](#1-słownik-na-start)
2. [JWT — co to jest naprawdę](#2-jwt--co-to-jest-naprawdę)
3. [OAuth 2.0 / OIDC — role i flow'y](#3-oauth-20--oidc--role-i-flowy)
4. [Architektura naszego systemu](#4-architektura-naszego-systemu)
5. [OpenIddict — co to i czemu właśnie to](#5-openiddict--co-to-i-czemu-właśnie-to)
6. [AuthSystem — Authorization Server krok po kroku](#6-authsystem--authorization-server-krok-po-kroku)
7. [TranslationSystem — Resource Server + leniwe prowizjonowanie](#7-translationsystem--resource-server--leniwe-prowizjonowanie)
8. [Frontend (Blazor SSR) — Client](#8-frontend-blazor-ssr--client)
9. [Service-to-service (client credentials)](#9-service-to-service-client-credentials)
10. [Bezpieczeństwo — co i czemu](#10-bezpieczeństwo--co-i-czemu)
11. [Jak to opowiadać na rozmowie — cheatsheet](#11-jak-to-opowiadać-na-rozmowie--cheatsheet)
12. [Pytania, które dostaniesz na 100%](#12-pytania-które-dostaniesz-na-100)
13. [Pliki do otwarcia, gdy zapytają „pokaż mi kod"](#13-pliki-do-otwarcia-gdy-zapytają-pokaż-mi-kod)

---

## 1. Słownik na start

- **Authentication (uwierzytelnianie)** — *kim jesteś*. Logowanie e-mailem i hasłem (ADR-0022;
  nazwa użytkownika służy tylko do wyświetlania).
- **Authorization (autoryzacja)** — *co możesz*. Role (`Admin`/`Translator`), scope'y (`api`/`service`).
- **Authorization Server (AS)** — wydaje tokeny. U nas: **`auth-api`** (OpenIddict + ASP.NET Identity).
- **Resource Server (RS)** — chroni dane i **waliduje** tokeny. U nas: **`tms-api`** (JwtBearer).
- **Client** — aplikacja, która prosi o token w imieniu użytkownika. U nas: **Frontend Blazor SSR**
  (i CLI, które pobiera publiczny plik bez tokena).
- **Claim** — fakt o użytkowniku w tokenie (`sub`, `name`, `email`, `role`).
- **Scope** — zakres dostępu, o który prosi klient (`openid`, `email`, `api`, …).
- **JWKS** — JSON Web Key Set, publiczny klucz, którym RS sprawdza podpis tokena.
- **PKCE** — Proof Key for Code Exchange, zabezpieczenie auth code flow dla klientów publicznych.

Jedno zdanie: **`auth-api` wydaje podpisane JWT, Frontend zdobywa je przez OIDC (auth code + PKCE),
a `tms-api` sprawdza podpis przez JWKS i autoryzuje po rolach.**

---

## 2. JWT — co to jest naprawdę

JWT (JSON Web Token) to trzy części w base64url złączone kropkami: `header.payload.signature`.

### 2.1 Header
Algorytm i id klucza:
```json
{ "alg": "RS256", "typ": "at+jwt", "kid": "signing-key-current" }
```
`kid` (`OpenIddictExtensions.cs`) mówi, którym kluczem RSA podpisano token — to ważne przy rotacji.
`typ` access tokena to `at+jwt` (RFC 9068).

### 2.2 Payload
Claimy. Access token niesie m.in. (`TokenEndpoint.cs`, `UserClaimDestinations.cs`):
```json
{
  "sub": "0190f3a2-....",        // id użytkownika (IdentityId) — jedyna referencja między kontekstami
  "name": "Aragorn",             // username → DisplayName w TMS
  "email": "aragorn@example.com",
  "role": ["Translator"],
  "scope": "openid email profile roles api offline_access",
  "iss": "https://localhost:5003/",
  "aud": ["lotrokoniecdev-api", "lotrokoniecdev-auth-api"],
  "exp": 1718800000
}
```
Kluczowe: `sub` to `IdentityId`, `role` decyduje o autoryzacji w `tms-api`. `aud` wymienia oba API,
bo strona wysyła ten sam token do `tms-api` i do endpointów konta w `auth-api`, a każde z nich
przyjmuje tylko token, który je wymienia (#1023). **`MapInboundClaims` jest wyłączony** w `tms-api`
i we Frontendzie, więc claimy zostają pod nazwami OpenIddict (`sub`/`name`/`email`/`role`), a nie
zamieniają się na długie `ClaimTypes.*`.

### 2.3 Signature
`RSASHA256(base64(header) + "." + base64(payload), klucz_prywatny)`. Tylko `auth-api` ma klucz
prywatny; każdy może sprawdzić podpis kluczem publicznym z JWKS.

### 2.4 Czemu to dobre
RS waliduje token **bez pytania bazy AS** — wystarczą podpis, JWKS i `exp`. Bez stanu, łatwo skalować,
a zmiana payloadu psuje podpis.

### 2.5 Czemu nie idealne
Access token jest **ważny do `exp`** i nie da się go łatwo odwołać. Dlatego ma **krótki żywot**
(5 min — ADR-0049), a **refresh tokeny są referencyjne** (w bazie, można je unieważnić — §10.2). Access
token **nie jest szyfrowany** (`DisableAccessTokenEncryption`): jest podpisany, ale czytelny, żeby
standardowa walidacja JwtBearer działała bez deszyfrowania.

---

## 3. OAuth 2.0 / OIDC — role i flow'y

### 3.1 Role
**Resource Owner** (użytkownik) · **Client** (Frontend) · **Authorization Server** (`auth-api`) ·
**Resource Server** (`tms-api`).

### 3.2 OIDC vs OAuth2
- **OAuth2** = autoryzacja (dostęp do zasobu). Daje **access token**.
- **OIDC** = warstwa tożsamości na OAuth2. Dodaje **id token** (kto się zalogował) i endpoint
  `userinfo`. Scope `openid` włącza OIDC.

### 3.3 Flow'y, które mamy w projekcie
Konfiguracja: `OpenIddictExtensions.cs`.

| Flow | Kto | Po co |
|---|---|---|
| **Authorization Code + PKCE** | Frontend (`lotrokoniecdev-web`) | interaktywne logowanie w przeglądarce |
| **Refresh Token** | Frontend | ciche odświeżanie access tokena (rolling, referencyjne) |
| **Client Credentials** | usługa do usługi (`lotrokoniecdev-api`) | maszyna do maszyny, bez użytkownika |
| **Password (ROPC)** | **tylko testy** (`lotrokoniecdev-test`) | testy integracyjne i E2E — włączony tylko w `Testing` |

`RequireProofKeyForCodeExchange()` wymusza PKCE w auth code flow.

---

## 4. Architektura naszego systemu

```
   Przeglądarka                Frontend (Blazor SSR, RP)         auth-api (OpenIddict AS)
   ───────────                 ─────────────────────────         ───────────────────────
        │  1. /auth/login           │                                    │
        │ ─────────────────────────▶│  2. Challenge → 302 do auth-api    │
        │ ──────────────────────────────────────────────────────────────▶│ 3. /connect/authorize
        │  4. login (cookie Identity, Razor /Account/Login)               │
        │ ◀──────────────────────────────────────────────────────────────│ 5. 302 z code do callback
        │ ─────────────────────────▶│  6. code → /connect/token (PKCE)   │
        │                           │ ──────────────────────────────────▶│ 7. access+id+refresh token
        │                           │  8. zapis tokenów w cookie sesji   │
        │                           │                                    │
        │                           │   tms-api (Resource Server, JwtBearer)
        │                           │   ────────────────────────────────
        │                           │  9. GET /api/v1/translations
        │                           │     Authorization: Bearer <access>  ──▶ walidacja podpisu (JWKS)
        │                           │                                         + sprawdzenie roli
```

### 4.1 Co siedzi gdzie
| Komponent | Projekt | Port (HTTPS dev) | Rola |
|---|---|---|---|
| `auth-api` | `AuthSystem.API` | `:5003` | OpenIddict AS + ASP.NET Identity (baza użytkowników) |
| `tms-api` | `TranslationSystem.API` | `:5002` | Resource Server (JwtBearer) |
| Frontend | `Frontend` | `:7017` | klient OIDC (cookie + OIDC) |

Jeden issuer (`auth-api`) obsługuje **przeglądarkę** i **back-channel** Frontendu i `tms-api`, więc
`iss` w tokenie zgadza się na każdej nodze. W dev wszystkie trzy aplikacje działają na hoście
(ADR-0006), a back-channel idzie po `https://localhost:5003` (`tms-api`: fallback
`Auth:Authority`→`Auth:Issuer`). W stackach kontenerowych (`compose.prod.yaml`, boksy Hetzner)
`tms-api` i Frontend wołają `auth-api` przez jego publiczny origin za Caddym, a nie po wewnętrznym
`http://auth-api:8080`, bo OpenIddict w Production odrzuca zwykły HTTP.

### 4.2 Klienci OAuth zarejestrowani w bazie
Seedowani na starcie (`DatabaseSeederExtensions.cs`), id z `AuthConstants.ClientIds`:

| `client_id` | Typ | Grants | Sekret | Uwagi |
|---|---|---|---|---|
| `lotrokoniecdev-web` | public | auth code+PKCE, refresh | brak | Frontend; redirect i post-logout URI z konfiguracji |
| `lotrokoniecdev-api` | confidential | client credentials | tak (`OpenIddict:ApiClientSecret`) | usługa do usługi; może wołać introspekcję |
| `lotrokoniecdev-test` | public | password, refresh | brak | **tylko `Testing`** |

---

## 5. OpenIddict — co to i czemu właśnie to

**OpenIddict** to biblioteka do budowy własnego serwera OAuth2/OIDC w ASP.NET Core. Nie jest
hostowana na zewnątrz (jak Auth0 czy Entra), tylko działa **u nas**, w `auth-api`. Czemu:
- Pełna kontrola nad tokenami, claimami i flow'ami, bez kosztu i limitów zewnętrznego dostawcy.
- Współpracuje z ASP.NET Core Identity (baza użytkowników, hasła, lockout, potwierdzanie e-maila).
- Na start był kopią sprawdzonego serwera z wcześniejszego projektu autora. Od tamtej pory rozwija
  się już tylko tutaj.

OpenIddict ma trzy części, wszystkie włączone w `OpenIddictExtensions.cs`:
- **Core** — magazyn EF Core na aplikacje, tokeny i scope'y (`UseDbContext<AuthDbContext>`).
- **Server** — endpointy `connect/*`, wydawanie i walidacja tokenów, klucze.
- **Validation** (`UseLocalServer`) — walidacja tokenów w samym `auth-api` (`userinfo`, `auth/*`);
  przyjmuje tylko audience `lotrokoniecdev-auth-api` (`AddAudiences`).

---

## 6. AuthSystem — Authorization Server krok po kroku

### 6.1 ASP.NET Core Identity (baza użytkowników)
`AddIdentityCore<ApplicationUser>` (`PersistenceDependencyInjection.cs`). `ApplicationUser` rozszerza
`IdentityUser<Guid>` o akceptacje regulaminu i polityki prywatności, plan usunięcia konta i dane do
cofnięcia zmiany e-maila (`ApplicationUser.cs`). Reguły:
- hasło: cyfra, mała litera, wielka litera i znak specjalny, `RequiredLength = 8`; maksimum 128 dokłada
  `PasswordMaxLengthValidator` (Identity nie ma takiej opcji; #1046), a `BreachedPasswordValidator`
  odrzuca hasło znane z wycieków (ADR-0066);
- `User.RequireUniqueEmail = true`;
- `SignIn.RequireConfirmedEmail = true` — **bez potwierdzenia maila nie zalogujesz się**;
- lockout: `MaxFailedAccessAttempts = 5`, `DefaultLockoutTimeSpan = 5 min`.

Tokeny potwierdzenia e-maila i resetu hasła żyją 24 h (`DataProtectionTokenProviderOptions`).

### 6.2 OpenIddict — konfiguracja serwera
`OpenIddictExtensions.cs`:
- endpointy: `connect/token`, `connect/authorize`, `connect/userinfo`, `connect/introspect`,
  `connect/revoke`, `connect/logout`;
- flow'y: refresh + client credentials + auth code (+ password tylko w `Testing`);
- `RequireProofKeyForCodeExchange()` — PKCE wymagane;
- `UseReferenceRefreshTokens()` — refresh tokeny **w bazie** (można je unieważnić, rolling — §10.2);
- `RegisterScopes(openid, email, profile, roles, offline_access, api, service)`;
- `DisableAccessTokenEncryption()` — access token podpisany, nieszyfrowany.

### 6.3 Klucze kryptograficzne
`OpenIddictExtensions.cs` (`ConfigureOpenIddictServerSettings`):
- **Development i Testing** → `AddEphemeralSigningKey()` + `AddEphemeralEncryptionKey()` — nowe przy
  każdym starcie (restart unieważnia stare tokeny, lokalnie to nie problem).
- **Każde inne środowisko (Staging też)** → klucze z konfiguracji:
  - **Signing** = RSA (min **2048** bitów, sprawdzane przy starcie), publiczna połówka idzie do JWKS;
  - **Encryption** = symetryczny AES (min **256** bitów) — dla auth code i refresh tokenów, nie
    trafia do JWKS;
  - **rotacja**: opcjonalny `PreviousRsaPrivateKeyXml` — tokeny podpisane starym kluczem są ważne
    w oknie rotacji (`kid` = `signing-key-previous`).

### 6.4 Endpointy OpenIddict
| Endpoint | Plik | Co robi |
|---|---|---|
| `connect/authorize` | `AuthorizeEndpoint.cs` | wejście auth code; challenge cookie Identity, buduje `ClaimsIdentity` z `sub`/`email`/`name`/`role`, `SignIn` |
| `connect/token` | `TokenEndpoint.cs` | wydaje tokeny; rozdziela grant (auth code / refresh / client credentials / password) |
| `connect/userinfo` | `UserInfoEndpoint.cs` | claimy użytkownika według przyznanych scope'ów (`email`/`profile`/`roles`) |
| `connect/logout` | `LogoutEndpoint.cs` | RP-initiated end-session: kończy **sesję tego urządzenia** — unieważnia autoryzację z `id_token_hint` i jej tokeny (§10.3), czyści cookie tej przeglądarki |
| `connect/revoke` | middleware OpenIddict | unieważnienie pojedynczego tokena. OpenIddict obsługuje je sam; trasa w `MiddlewareServedEndpoints.cs` niesie tylko metadane rate-limit (#349) |
| `connect/introspect` | middleware OpenIddict | introspekcja tokena (RFC 7662) dla klientów confidential; trasa jak wyżej, tylko POST (ADR-0061) |

`UserClaimDestinations` decyduje, które claimy trafiają do access tokena, a które do id tokena.
`sub`/`email`/`name`/`role` trafiają do obu.

### 6.5 Register endpoint (nasz) — i **brak sagi**
`POST auth/register` (`RegisterUser.cs`, `AllowAnonymous`, `register-limit`):
1. walidacja: e-mail unikalny, regex, ≤250 — **to e-mail jest loginem** (ADR-0022); username unikalny,
   tylko litery i cyfry, ≤150 (`UsernameConstants`); hasło 8–128 ze złożonością i spoza wycieków;
   **obie akceptacje `true`** (polityka prywatności i regulamin). Zgody na przetwarzanie danych nie ma
   (#1073, ADR-0068);
2. odpowiedź nie zdradza, czy adres ma konto. Wolny adres: `userManager.CreateAsync`, rola
   **`Translator`** i wiadomość `EmailConfirmationRequested` w outboxie, w tej samej transakcji. Zajęty
   adres: konto nie powstaje, a właściciel dostaje powiadomienie (`RegistrationAttemptedOnExistingAccount`).
   Adres niedawno zwolniony przez zmianę e-maila innego konta: nic się nie dzieje (#684);
3. maile wysyła relay outboxu po commicie (ADR-0035, ADR-0038), więc rejestracja nie dotyka SMTP;
4. zwraca **202** bez treści, zawsze po co najmniej 1 s (ADR-0059). Na jedną skrzynkę przypadają
   3 rejestracje na 15 minut (ADR-0057).

**Kluczowe:** *nie ma* sagi między kontekstami, która przy rejestracji tworzyłaby też profil w TMS
(ADR-0002 §7). Profil tłumacza w TMS powstaje **leniwie** przy pierwszym uwierzytelnionym żądaniu
(§7.3, ADR-0004). Dobry temat na rozmowę: „dlaczego rozdzieliliście rejestrację od profilu
domenowego".

### 6.6 Skąd się biorą roles i scopes w tokenie
- **roles**: z `userManager.GetRolesAsync(user)` przy wydawaniu tokena (`TokenEndpoint.cs`,
  `AuthorizeEndpoint.cs`), wstawiane jako claimy `role`.
- **scopes**: z żądania klienta (`request.GetScopes()`), ograniczone uprawnieniami klienta w bazie.
  `SetResources(UserTokenAudiences.All)` ustawia `aud` na `lotrokoniecdev-api` (`tms-api`)
  i `lotrokoniecdev-auth-api` (`auth-api`). Wymiana kodu i refresh ustawiają tę listę od nowa, zamiast
  brać ją z kodu albo refresh tokena — powód jest w `UserTokenAudiences` (#1023).

Role seedowane (`DatabaseSeederExtensions.cs`): **`Admin`** i **`Translator`**.

---

## 7. TranslationSystem — Resource Server + leniwe prowizjonowanie

### 7.1 Konfiguracja JwtBearer
`ApiDependencyInjection.cs`. Standardowy `AddJwtBearer` (nie walidacja OpenIddict — prościej):
- `Authority = EffectiveAuthority` (`Auth:Authority`, a gdy go brak — `Auth:Issuer`);
- `Audience = "lotrokoniecdev-api"`;
- `TokenValidationParameters`: issuer + audience + lifetime + klucz podpisu, bez tolerancji zegara
  (`ClockSkew = 0`) i tylko typ `at+jwt` (#933, ADR-0049);
- `NameClaimType = "name"`, `RoleClaimType = "role"`, `MapInboundClaims = false`;
- **klucze z JWKS**: `ConfigurationManager` pobiera `{authority}/.well-known/openid-configuration`
  — `tms-api` zna tylko klucz publiczny;
- `RequireHttpsMetadata` jest wyłączony w Development i Testing oraz gdy issuer to `http://`.

### 7.2 Authorization Policies
`ApiDependencyInjection.cs` (`AddAuthorizationPolicies`):

| Policy | Reguła |
|---|---|
| **fallback** | `RequireAuthenticatedUser()` — dla każdego endpointu bez własnych metadanych |
| `RequireAdminRole` | rola `Admin` |
| `RequireTranslatorRole` | rola `Admin` **lub** `Translator` |
| `RequireAuthenticatedUser` | zalogowany, dowolna rola (eksport danych tłumacza, ADR-0032) |
| `ApiScope` / `RequireServiceScope` | claim `scope` zawiera `api` / `service` (`HasScope`); dziś żaden endpoint ich nie używa |

Endpointy są **autoryzowane domyślnie** — anonim dostaje 401. Publiczne endpointy mają jawne
`AllowAnonymous`: dokument usług `GET /`, pobranie pliku, `GET /progress`, lista tłumaczeń i health.

### 7.3 Leniwe, idempotentne prowizjonowanie tłumacza (ADR-0004) — nasza specyfika

`tms-api` potrzebuje **lokalnej** tożsamości tłumacza (`Translator`), żeby pokazać „submitted by /
approved by <name>" dla *innych* tłumaczy — JWT bieżącego użytkownika tego nie rozwiąże. Zamiast sagi
przy rejestracji: **get-or-create przy pierwszym uwierzytelnionym żądaniu**.

`TranslatorProvisioner.ProvisionCurrentAsync`:
1. czyta `IdentityId` z claimu `sub` (`CurrentUserAccessor.cs`); brak → `Forbidden`;
2. buduje `DisplayName` z claimu `name` (a gdy go brak — z `email`) i opcjonalny `Email`;
3. **get-or-create** po `IdentityId`: wiersz istnieje i claimy się zmieniły → `RefreshProfile`; nie
   istnieje → `Create` i insert;
4. **wyścig pierwszego zapisu**: `DbUpdateException` (unikalny indeks `Translators.IdentityId`) ⇒ odpina
   swój insert, czyta zapisany wiersz i go odświeża.

Wynik siedzi w `HybridCache` na 5 minut, z kluczem zależnym od odcisku claimów `name` i `email`
(PERF-07, #437). W zwykłym ruchu nie kosztuje zapytania do bazy; zmiana claimów zmienia odcisk
i odświeża wiersz.

Dwa miejsca wywołania (ADR-0004, aneks 2026-06-24):

- **od razu**: `TranslatorProvisioningMiddleware` (za `UseAuthorization`) tworzy profil każdemu
  zalogowanemu przy pierwszym żądaniu. Porażka `Result` jest logowana i pomijana, więc odczyty od niej
  nie zależą;
- **na pewno**: handlery zapisu, które stemplują `TranslatorId` — upsert, approve i bulk approve. Tu
  porażka wraca do klienta jako błąd.

Odczyt cudzych nazw to join read modelu z wierszami `Translator`, bez sięgania do Auth. Po usunięciu
konta w Auth profil traci nazwę i e-mail (`Translator.Erase`, ADR-0065).

> Łańcuch referencji: `Translation.SubmittedById/ApprovedById → Translator.Id`, a
> `Translator.IdentityId → użytkownik Auth`. Jeden wiersz `Translator` zamiast kopii nazwy na każdym
> wierszu — po zmianie nazwy nic się nie rozjeżdża. Szczegóły: [DOMAIN.md §Translator](DOMAIN.md#3-translator-adr-0004).

### 7.4 Użycie w endpointach
```csharp
endpointRouteBuilder.MapPost("/api/v1/translations/{id:guid}/approve", /* … */)
    .RequireAuthorization(AuthorizationPolicies.RequireAdminRole);   // ApproveTranslation.cs
```
Linki HATEOAS pokazują się tylko temu, kto może za nimi pójść: `ILinkFactory` sprawdza politykę
docelowego endpointu (ADR-0040), a slice dodaje reguły stanu (np. `approve` tylko na `Draft` /
`NeedsReview`).

---

## 8. Frontend (Blazor SSR) — Client

ADR-0067 ustala, kto co sprawdza: Frontend jest klientem i sprawdza tylko to, czego OpenID Connect
wymaga od klienta (robi to handler OIDC). Access token jest dla niego nieprzezroczysty — sprawdza go
`tms-api`.

### 8.1 Schemat: cookies + OIDC
`AuthenticationDependencyInjectionExtensions.cs`. Dwa schematy:
- **Cookie** (`DefaultScheme` + `DefaultChallengeScheme`) — sesja po zalogowaniu
  (`.lotrokoniecdev.auth`, HttpOnly, SameSite Lax, 8 h z przesuwaniem). Strona, która wymaga
  logowania, robi challenge cookie, czyli 302 na `/auth/login?ReturnUrl=…` (`LoginPath`), nigdy prosto
  do `auth-api` (#1153);
- **OpenIdConnect** (`DefaultSignOutScheme`) — challenge do `auth-api` robi jawnie `/auth/login`.

Po zalogowaniu przeglądarka nosi **cookie**, nie token. Tokeny (access/refresh/id) leżą w
zaszyfrowanym cookie sesji (`SaveTokens = true`).

### 8.2 Konfiguracja OIDC
`AuthenticationDependencyInjectionExtensions.cs`:
- `Authority = settings.Authority` (`auth-api`), `ClientId = settings.ClientId` (`lotrokoniecdev-web`);
- `ResponseType = code`, `ResponseMode = query`, `UsePkce = true`, `SaveTokens = true`. `query` jest
  konieczne: strona `form_post` OpenIddict wysyła się skryptem inline, który blokuje CSP `auth-api`
  (#693);
- `GetClaimsFromUserInfoEndpoint = true` — dociąga claimy z `connect/userinfo`;
- `MapInboundClaims = false`, `NameClaimType = "name"`, `RoleClaimType = "role"`;
- `CallbackPath` / `SignedOutCallbackPath` i scope'y z konfiguracji;
- `OnValidatePrincipal` woła `CookieTokenRefresher` — odświeża wygasający access token na każdym
  żądaniu poza `POST /auth/logout` i `POST /auth/local-signout` (§8.4);
- `OnRemoteFailure` / `OnAccessDenied` → własne strony błędów (z trace ID).

### 8.3 Login / logout flow
`AuthEndpointsExtensions.cs`:
- **Login**: `GET /auth/login?returnUrl=…` → `Results.Challenge` na schemat OIDC → 302 do
  `connect/authorize`. `returnUrl` sprawdza `LocalReturnUrl.Sanitize` (ochrona przed open redirect).
  Najpierw `SignInAvailability` wczytuje discovery document przez configuration manager handlera OIDC.
  Gdy `auth-api` nie odpowiada, a Frontend nie ma jeszcze kopii dokumentu, trasa zwraca 503 i strona
  mówi „Logowanie chwilowo niedostępne" (#311). Z kopią w pamięci użytkownik idzie dalej do
  `auth-api` (#1208). Tędy przechodzi każda strona wymagająca logowania (#1153) i nieudany powrót
  z logowania (`OnRemoteFailure`).
- **Logout**: `POST /auth/logout` → `SignOutAsync(cookie)`, potem Frontend sam unieważnia swój refresh
  token w `connect/revoke` (serwer–serwer, `RefreshTokenRevoker`, #964), a na koniec przekierowuje na
  `connect/logout` z `id_token_hint` i `post_logout_redirect_uri`. `auth-api` **kończy sesję tego
  urządzenia** (§10.3); sesje na innych urządzeniach żyją dalej.

### 8.4 CookieTokenRefresher
`Infrastructure/Auth/TokenRefresh/CookieTokenRefresher.cs`, na `OnValidatePrincipal`. Gdy access token
zbliża się do końca, wymienia refresh token w `connect/token` (przez `ITokenEndpointClient`) i zapisuje
nową sesję. Rolling refresh: stary refresh token jest unieważniony, nowy zapisany. Żywa sesja, której
nie trzeba odnawiać, w ogóle nie woła `auth-api`.

Nieudany refresh jest chwilowy albo ostateczny (ADR-0067 decyzja 3, #1062):
- **chwilowy** (429, każdy 5xx, błąd sieci, timeout 30 s z #923) nie kończy sesji, dopóki bieżący
  access token działa. Strona niczego nie unieważnia, nic nie pokazuje i próbuje znowu przy następnym
  żądaniu. Okno to reszta życia tokena, około minuty;
- **ostateczny** (każda inna odmowa, np. 400 `invalid_grant`, nieczytelna odpowiedź 2xx, albo chwilowy
  błąd już po wygaśnięciu tokena) kończy sesję od razu.

Z odpowiedzi refreshu strona czyta tylko token i `expires_in`. Nie sprawdza kształtu ani znaków
tokenów, nie waliduje odnowionego id tokena, a access tokena nie sprawdza wcale: ani podpisu, ani
kształtu, ani `exp` (ADR-0067 decyzja 2, #1058).

**Martwa sesja.** Odmowę tokena widać na dwa sposoby: 401 z endpointu, który wymaga logowania, albo
dokument usług (`GET /`, otwarty dla każdego) bez linków dla zalogowanego. Każda zostawia znacznik
w `DeadSessionRegistry`, a następne żądanie kończy po nim sesję (czysty re-login). Tak kończy się
też sesja po twardej wymianie klucza podpisu: pierwsza strona, która woła endpoint wymagający
logowania (np. „Panel”), pokazuje błąd zamiast danych, a następna kończy sesję z komunikatem
o wygaśnięciu. Znacznik wisi pod
skrótem SHA-256 odrzuconego access tokena, nie pod id użytkownika, więc kończy się tylko sesja
urządzenia z tym tokenem (#1061). Strony publiczne same tego nie wykryją, bo otwarty endpoint traktuje
zły token jak gościa; wtedy sesję kończy najpóźniej odnowienie, po najwyżej 5 minutach.

**Sprzątanie.** Gdy strona sama kończy sesję (znacznik martwej sesji, ostatecznie nieudany albo
nieczytelny refresh), unieważnia też refresh token z cookie przez `RefreshTokenRevoker` (#1027), bo
samo usunięcie cookie zostawiłoby go ważnym na serwerze przez godziny. Przy nieczytelnej odpowiedzi
unieważnia także nowy token z tej odpowiedzi. Oba wywołania mają wspólny limit 5 s. Wyjątek:
przeglądarka zerwała żądanie w trakcie takiego refreshu — wtedy nie dostanie usuniętego cookie
i wróci z tym samym tokenem, więc token zostaje ważny. Na `POST /auth/logout` i
`POST /auth/local-signout` refresher niczego nie sprawdza, żeby wylogowanie unieważniło bieżący,
niezużyty refresh token (§10.3, #964, #1027).

### 8.5 Wywołania do API z tokenem
`TranslationContentNegotiationAndAuthDelegatingHandler` dokłada `Authorization: Bearer <access>` (z
sesji) i nagłówek `Accept` HATEOAS do każdego wywołania `tms-api`. `SameOriginDelegatingHandler`
(pierwszy w łańcuchu) nie puszcza żądania na inny origin niż skonfigurowany, a
`FrontendCallerDelegatingHandler` dokłada adres odwiedzającego i klucz Frontendu do limitów
(ADR-0054). Frontend używa `TranslationSystem.Contracts` bezpośrednio (te same DTO).

---

## 9. Service-to-service (client credentials)

Gdy usługa musi zawołać `tms-api` bez użytkownika: grant **client_credentials** klienta
`lotrokoniecdev-api` (confidential, z sekretem). `TokenEndpoint.HandleClientCredentialsGrant` buduje
`ClaimsIdentity` z `sub = client_id`, scope'ami z żądania i `aud = lotrokoniecdev-api`. W `tms-api`
taki token przeszedłby policy `RequireServiceScope` (scope `service`), gdyby slice jej wymagał — dziś
żaden nie wymaga.

W samym `auth-api` taki token nie wejdzie na żaden endpoint z bearerem (`auth/*` z logowaniem i
`connect/userinfo`). Walidacja OpenIddict w `auth-api` przyjmuje tylko audience
`lotrokoniecdev-auth-api`, a token usługi ma tylko `lotrokoniecdev-api`, więc dostaje 401
`invalid_token`, zanim zadziała jakakolwiek policy (#1023). Gdyby token klienta kiedyś wymienił
`auth-api`, są jeszcze dwie warstwy: domyślna policy `UserTokenPolicy` (`sub` musi być GUID-em
użytkownika, inaczej 403 — #966) i własne sprawdzenie w `connect/userinfo` (`invalid_token` — #955).

---

## 10. Bezpieczeństwo — co i czemu

### 10.1 PKCE
`RequireProofKeyForCodeExchange()`. Klient publiczny (Frontend, bez sekretu) generuje `code_verifier`,
wysyła `code_challenge = SHA256(verifier)` przy `authorize`, a przy wymianie `code → token` pokazuje
`verifier`. Przechwycony kod jest wtedy bezużyteczny.

### 10.2 Rolling reference refresh tokens
`UseReferenceRefreshTokens()`. Refresh tokeny są **referencyjne** (zapisane w bazie, a nie
samowystarczalne), więc można je **unieważnić**. Rolling: użycie refresh tokena unieważnia stary
i wydaje nowy, co ogranicza replay. Logout unieważnia tokeny sesji tego urządzenia (`LogoutEndpoint.cs`
→ `IUserSessionRevoker`). Refresh token żyje **9 h** od ostatniego użycia: każdy nowy token dostaje
znów pełne 9 h. Cookie strony, w którym Frontend trzyma token, wygasa po 8 h bezczynności, więc
dłuższy czas nic nie daje, a godzina zapasu pilnuje, żeby token nie umarł przed cookie (#1014).

### 10.3 Token revocation przy logout
Logout kończy **tylko sesję tego urządzenia** (decyzja ownera w #931). Każde logowanie przez stronę
dostaje od OpenIddict własną autoryzację (ad hoc, bo `AuthorizeEndpoint` żadnej nie tworzy sam),
a refresh zachowuje jej id: jedno logowanie = jedna autoryzacja. `LogoutEndpoint.cs` bierze jej id
z `id_token_hint`. Wcześniejsze logowanie na tym samym urządzeniu, którego sesja strony już wygasła,
ma własną autoryzację i logout jej nie kończy.

OpenIddict sprawdza podpis hintu, to, że token i jego autoryzacja są wciąż ważne w bazie, i to, że
hint należy do klienta, do którego należy `post_logout_redirect_uri`. Nie sprawdza czasu ważności:
stary id token działa, dopóki jego sesja żyje. Potem `IUserSessionRevoker.RevokeSessionAsync`
unieważnia autoryzację, a następnie jej tokeny jednym bulk update (`RevokeByAuthorizationIdAsync`).
Na koniec `SignOutAsync` czyści cookie `auth-api` tej przeglądarki i usuwa z bazy jego sesję
(ADR-0062): cookie niesie tylko klucz sesji, a sama sesja leży w tabeli `SignInSessions`
(`SignInSessionTicketStore`), więc skopiowane cookie przestaje działać razem z oryginałem. Każde
logowanie dostaje nową sesję (`SignInSessionCookieHandler` usuwa najpierw sesję starego cookie).
Cookie Identity należy do przeglądarki, a nie do jednego logowania strony, dlatego logout bez ważnego
hintu niczego nie unieważnia, tylko kończy sesję cookie. Frontend zawsze wysyła hint, gdy ma sesję.

Przeglądarka może nie dotrzeć do `connect/logout` (zamknięta karta, zerwana sieć, restart
`auth-api`). Dlatego Frontend przed przekierowaniem sam unieważnia swój refresh token (#964):
`RefreshTokenRevoker` bierze `revocation_endpoint` z discovery (nigdy własnej ścieżki) i wysyła
`token` + `token_type_hint=refresh_token` + `client_id` przez `ITokenEndpointClient`. Ma limit 5 s,
nie zależy od `RequestAborted` (zamknięta karta to właśnie ten przypadek), a błąd tylko loguje. Na
żądaniu wylogowania refresher nie odświeża tokena: odświeżenie zużyłoby bieżący refresh token, który
OpenIddict przyjmuje jeszcze przez okno reuse leeway (domyślnie 30 s), więc unieważnienie trafiłoby
w nowy token, a nie w ten z kopii cookie.

Na innych urządzeniach żyją i sesje strony, i cookie `auth-api` — tak ma być. Wszystkie sesje naraz
kończy zmiana hasła (a także reset, zmiana e-maila i usunięcie konta), bo zmienia security stamp.

### 10.4 Ochrona przed timing attack
Gdy użytkownik nie istnieje, `TokenEndpoint.cs` i strona logowania i tak sprawdzają hasło z **dummy
hashem**, żeby czas odpowiedzi nie zdradzał „nie ma użytkownika" kontra „złe hasło". Anonimowe
odpowiedzi, które ukrywają istnienie konta, czekają też do minimalnego czasu (`IResponseTimeFloor`,
ADR-0059: 500 ms, rejestracja 1 s, ponowne wysłanie potwierdzenia 3 s). Forgot password i resend
confirmation zawsze zwracają sukces.

### 10.5 HTTPS i metadata
W `tms-api` `RequireHttpsMetadata` jest włączony poza Development i Testing, gdy issuer to `https://`, więc JWKS
i discovery idą tylko po HTTPS.

### 10.6 Cookie security
`.lotrokoniecdev.auth`: `HttpOnly` (JS go nie czyta), `SameSite=Lax` (ochrona przed CSRF),
`SecurePolicy = Always` poza Development (TLS kończy się na proxy, więc `SameAsRequest` gubił flagę
`Secure` — AUDIT-SEC-10, #400), `Path=/` (logowanie i wylogowanie zawsze trafiają w to samo cookie).
8 h z przesuwaniem.

### 10.7 Akceptacje przy rejestracji (umowa, nie zgoda) i kasowanie konta
`RegisterUser.cs`: `acceptedPrivacyPolicy` i `acceptedTermsOfService` **muszą być `true`**, a data
każdej akceptacji trafia na `ApplicationUser`. Zgody na przetwarzanie danych nie ma (#1073): konto
działa na podstawie umowy, nie zgody (ADR-0068).
`DeleteAccount` **planuje** kasowanie RODO z 14-dniowym oknem anulowania (ADR-0031): konto zablokowane
na czas okna, sesje i tokeny unieważnione, jednorazowy link anulowania mailem przez outbox. Usunięcie
wykonuje finalizer po oknie (usuwa też zapisane sesje cookie `auth-api` — ADR-0062 — i powiadamia TMS
— ADR-0065), a anulowanie wymusza reset hasła.
`GET auth/account/data-export` to widok konta dla „Moje konto"; eksport danych RODO to
`POST auth/account/data-export` za aktualnym hasłem (ADR-0052).

### 10.8 Walidacja kluczy i ustawień
`ConfigureOpenIddictServerSettings`: RSA min 2048 bitów, AES min 256 bitów, czytelne wyjątki przy złym
formacie base64/XML. `OpenIddictSettingsValidator` sprawdza ustawienia w każdym środowisku poza
Development i Testing, więc Staging też musi mieć klucze, sekret klienta API i adresy przekierowań.

---

## 11. Jak to opowiadać na rozmowie — cheatsheet

### Wersja 30-sekundowa
> „Mam własny serwer OAuth2/OIDC na OpenIddict + ASP.NET Identity (`auth-api`). Frontend Blazor SSR
> jest klientem OIDC — loguje użytkownika przez authorization code + PKCE i trzyma tokeny w sesji za
> cookie. API tłumaczeń (`tms-api`) to resource server: sprawdza podpisany JWT przez JWKS i autoryzuje
> po rolach. Profil tłumacza w domenie powstaje **leniwie** przy pierwszym uwierzytelnionym żądaniu,
> a nie sagą przy rejestracji — czysty podział bounded contextów."

### Wersja 2-minutowa
Dorzuć: referencyjne rolling refresh tokeny (unieważniane przy logoucie), `MapInboundClaims=false`
(claimy pod nazwami OpenIddict), klucze jednorazowe w dev / RSA z rotacją w prod, fallback policy =
autoryzacja domyślnie, role `Admin`/`Translator`, i czemu leniwe prowizjonowanie (odczyt pokazuje cudze
nazwy → join read modelu; zapis stempluje FK → get-or-create przy pierwszym dotknięciu, idempotentny
dzięki unikalnemu indeksowi).

### Skróty, które trzeba znać
AS / RS / RP · JWT (`header.payload.signature`) · JWKS · `sub`/`aud`/`iss`/`exp` · PKCE · scope vs role
· auth code vs client credentials · reference vs self-contained token · rolling refresh.

---

## 12. Pytania, które dostaniesz na 100%

**Q: Czemu JWT, a nie sesja w bazie?** — RS waliduje bez stanu (podpis + JWKS + exp), bez pytania AS.
Łatwo skalować. Koszt: trudno unieważnić access token ⇒ krótki żywot i refresh tokeny, które można
unieważnić.

**Q: Co to PKCE i czemu potrzebne?** — Klient publiczny nie ma sekretu. PKCE (`code_verifier` +
`code_challenge`) dowodzi, że kod wymienia ten sam klient, który zaczął flow ⇒ przechwycony kod jest
bezużyteczny.

**Q: Jak waliduje się token po stronie API?** — `JwtBearer` pobiera JWKS z
`{authority}/.well-known/openid-configuration` i sprawdza podpis, `iss`, `aud`, `exp` (bez tolerancji
zegara) i typ `at+jwt` (#933). Klucza prywatnego RS nie zna.

**Q: Co się dzieje przy logoucie?** — Wylogowanie z cookie, unieważnienie refresh tokena strony
serwer–serwer (#964) i RP-initiated end-session w `auth-api`, który **unieważnia autoryzację i tokeny
sesji tego urządzenia** (§10.3). Inne urządzenia zostają zalogowane. Krótki access token wygaśnie sam.

**Q: Czemu refresh tokeny są referencyjne?** — Żeby dało się je **unieważnić** (są w bazie) i żeby
rolling ograniczał replay. Tokena samowystarczalnego nie da się unieważnić przed wygaśnięciem.

**Q: Czemu OpenIddict, a nie gotowy IdP?** — Pełna kontrola i brak zależności oraz kosztu u
zewnętrznego dostawcy. Na start był to sprawdzony serwer skopiowany z wcześniejszego projektu autora;
od tamtej pory rozwija się już tylko tutaj.

**Q: Jak działa service-to-service?** — Grant client_credentials klienta `lotrokoniecdev-api`
(confidential, z sekretem); token bez użytkownika, `sub = client_id`, autoryzacja po scope `service`.

**Q: Czym scope różni się od roli?** — Scope = na co klient prosi o pozwolenie (`api`, `email`); rola
= uprawnienie użytkownika (`Admin`/`Translator`). Scope ogranicza *klienta*, rola *użytkownika*.

**Q: Co jak ktoś zmieni payload JWT?** — Podpis przestaje pasować i `JwtBearer` odrzuca token (401).
Bez klucza prywatnego nie da się podpisać go na nowo.

**Q: Co to leniwe prowizjonowanie i czemu zamiast sagi?** — Rejestracja tworzy tylko użytkownika Auth;
lokalny `Translator` w TMS powstaje przy pierwszym uwierzytelnionym żądaniu (middleware bez gwarancji
+ na pewno w handlerach zapisu; get-or-create idempotentny po `IdentityId`, unikalny indeks chroni przed
wyścigiem). Odczyt w ogóle nie zależy od Auth; bounded contexty są rozdzielone (ADR-0004 z aneksem).

**Q: Co jak AuthSystem padnie?** — Nowe logowania i refreshe nie działają, ale wydane access tokeny
działają do `exp`. `tms-api` trzyma JWKS w cache, więc walidacja przeżywa krótką przerwę `auth-api`.
Frontend nie sprawdza access tokena sam, więc strony zalogowanego użytkownika, które nie wołają
`auth-api` (wszystko poza „Moje konto"), działają nawet po restarcie Frontendu, gdy `auth-api` leży
(#1058). Działają, dopóki access token jest ważny (do 5 minut): potem odnowienie musi zawołać
`auth-api`, a gdy ten dalej leży, sesja się kończy.

---

## 13. Pliki do otwarcia, gdy zapytają „pokaż mi kod"

| Temat | Plik |
|---|---|
| Konfiguracja serwera OpenIddict | `AuthSystem.API/Extensions/OpenIddictExtensions.cs` |
| Wydawanie tokenów (granty) | `AuthSystem.API/Features/Auth/TokenEndpoint.cs` |
| Wejście auth code | `AuthSystem.API/Features/Auth/AuthorizeEndpoint.cs` |
| Logout + unieważnienie | `AuthSystem.API/Features/Auth/LogoutEndpoint.cs` |
| Rejestracja (bez sagi) | `AuthSystem.API/Features/Auth/RegisterUser.cs` |
| Seed klientów, ról i scope'ów | `AuthSystem.API/Extensions/DatabaseSeederExtensions.cs` |
| Identity (hasło, lockout, e-mail) | `AuthSystem.Persistence/PersistenceDependencyInjection.cs` |
| JwtBearer + policies (RS) | `TranslationSystem.API/ApiDependencyInjection.cs` |
| Leniwe prowizjonowanie | `TranslationSystem.API/Auth/Provisioning/TranslatorProvisioner.cs` |
| Prowizjonowanie przy żądaniu (middleware) | `TranslationSystem.API/Middleware/TranslatorProvisioningMiddleware.cs` |
| Odczyt tożsamości z tokena | `TranslationSystem.API/Auth/CurrentUserAccessing/CurrentUserAccessor.cs` |
| Klient OIDC (cookie + OIDC) | `Frontend/Infrastructure/Auth/AuthenticationDependencyInjectionExtensions.cs` |
| Login/logout po stronie Frontendu | `Frontend/Infrastructure/Auth/AuthEndpointsExtensions.cs` |
| Refresh tokena we Frontendzie | `Frontend/Infrastructure/Auth/TokenRefresh/CookieTokenRefresher.cs` |

Powiązane: [API.md](API.md) (kontrakty HTTP), [INVARIANTS.md §11](INVARIANTS.md#11-authsystem--uwierzytelnianie)
(reguły auth), [DOMAIN.md §Translator](DOMAIN.md#3-translator-adr-0004) (model tożsamości tłumacza),
ADR-0002 §6–7 (auth od pierwszego dnia, brak sagi), ADR-0004 (agregat `Translator`), ADR-0067 (kto co
sprawdza).
