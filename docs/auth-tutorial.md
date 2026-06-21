# Tutorial: Auth w LotroKoniecDev TMS — od zera do rozmowy rekrutacyjnej

> Pełny przewodnik po uwierzytelnianiu i autoryzacji w TMS, napisany **z kodu** (`AuthSystem.API`,
> `TranslationSystem.API/Auth`, OIDC RP we `Frontend`), nie z teorii. Cel podwójny: (1) zrozumieć
> system na tyle, by go rozwijać; (2) umieć go opowiedzieć na rozmowie. **Kod jest źródłem prawdy** —
> przy rozbieżności czytaj plik i popraw ten dokument. Lokalizacje jako `plik:linia`.
>
> Skróty kontraktów HTTP: [API.md §2 + §7](API.md#2-authentication--authorization-model). Reguły:
> [INVARIANTS.md §11](INVARIANTS.md#11-authsystem--uwierzytelnianie).

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

- **Authentication (uwierzytelnianie)** — *kim jesteś*. Logowanie loginem + hasłem.
- **Authorization (autoryzacja)** — *co możesz*. Role (`Admin`/`Translator`), scope'y (`api`/`service`).
- **Authorization Server (AS)** — wydaje tokeny. U nas: **`auth-api`** (OpenIddict + ASP.NET Identity).
- **Resource Server (RS)** — chroni dane, **waliduje** tokeny. U nas: **`tms-api`** (JwtBearer).
- **Client** — aplikacja proszące o token w imieniu usera. U nas: **Blazor SSR Frontend** (i CLI).
- **Claim** — fakt o userze w tokenie (`sub`, `name`, `email`, `role`).
- **Scope** — zakres dostępu, o który prosi klient (`openid`, `email`, `api`, …).
- **JWKS** — JSON Web Key Set, publiczny klucz, którym RS sprawdza podpis tokena.
- **PKCE** — Proof Key for Code Exchange, zabezpieczenie auth code flow dla klientów publicznych.

Jedno zdanie: **`auth-api` wydaje podpisane JWT, Frontend zdobywa je przez OIDC (auth code + PKCE),
a `tms-api` waliduje podpis przez JWKS i autoryzuje po rolach.**

---

## 2. JWT — co to jest naprawdę

JWT (JSON Web Token) to trzy base64url-owe części złączone kropkami: `header.payload.signature`.

### 2.1 Header
Algorytm i id klucza:
```json
{ "alg": "RS256", "typ": "JWT", "kid": "signing-key-current" }
```
`kid` (`OpenIddictExtensions.cs:155`) wskazuje, którym kluczem RSA podpisano — ważne przy rotacji.

### 2.2 Payload
Claimy. U nas access token niesie m.in. (`TokenEndpoint.cs:195-203`):
```json
{
  "sub": "0190f3a2-....",        // id usera (IdentityId) — jedyna referencja cross-context
  "name": "Aragorn",             // username → DisplayName w TMS
  "email": "aragorn@example.com",
  "role": ["Translator"],
  "scope": "openid email profile roles api offline_access",
  "iss": "https://localhost:5003/",
  "aud": "lotrokoniecdev-api",
  "exp": 1718800000
}
```
Kluczowe: `sub` to `IdentityId`, `role` napędza autoryzację w `tms-api`. **`MapInboundClaims` jest
wyłączony** po obu stronach (`ApiDependencyInjection.cs:168`), więc claimy zostają surowymi typami
OpenIddict (`sub`/`name`/`email`/`role`), a nie mapują się na długie `ClaimTypes.*`.

### 2.3 Signature
`RSASHA256(base64(header) + "." + base64(payload), klucz_prywatny)`. Tylko `auth-api` ma klucz
prywatny; każdy może sprawdzić podpis kluczem publicznym z JWKS.

### 2.4 Czemu to genialne
RS waliduje token **bez odpytywania bazy AS** — sam podpis + JWKS + `exp` wystarczą. Stateless,
skalowalne, odporne na podmianę (zmiana payloadu psuje podpis).

### 2.5 Czemu nie genialne
Access token jest **ważny do `exp`** i nie da się go łatwo odwołać (stateless). Dlatego **krótki
żywot** (60 min) + **refresh tokeny referencyjne** (w bazie, rewokowalne — §10.2). U nas dodatkowo
access token **nie jest szyfrowany** (`DisableAccessTokenEncryption`, `OpenIddictExtensions.cs:64`) —
podpisany (tamper-proof), ale czytelny — żeby standardowa walidacja JwtBearer działała bez
deszyfrowania.

---

## 3. OAuth 2.0 / OIDC — role i flow'y

### 3.1 Role
**Resource Owner** (user) · **Client** (Frontend) · **Authorization Server** (`auth-api`) ·
**Resource Server** (`tms-api`).

### 3.2 OIDC vs OAuth2
- **OAuth2** = autoryzacja (dostęp do zasobu). Daje **access token**.
- **OIDC** = warstwa tożsamości na OAuth2. Dodaje **id token** (kto się zalogował) + endpoint
  `userinfo`. Scope `openid` włącza OIDC.

### 3.3 Flow'y, które mamy w projekcie
Konfiguracja: `OpenIddictExtensions.cs:33-43`.

| Flow | Kto | Po co |
|---|---|---|
| **Authorization Code + PKCE** | Frontend (`lotrokoniecdev-web`) | interaktywne logowanie usera w przeglądarce |
| **Refresh Token** | Frontend | ciche odświeżanie access tokena (rolling, referencyjne) |
| **Client Credentials** | service-to-service (`lotrokoniecdev-api`) | maszyna-do-maszyny, bez usera |
| **Password (ROPC)** | **tylko testy** (`lotrokoniecdev-test`) | integracyjne/E2E — w innych środowiskach OpenIddict odrzuca |

`RequireProofKeyForCodeExchange()` (`OpenIddictExtensions.cs:45`) wymusza PKCE dla auth code flow.

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
        │                           │                                         + role check
```

### 4.1 Co siedzi gdzie
| Komponent | Projekt | Port (HTTPS dev) | Rola |
|---|---|---|---|
| `auth-api` | `AuthSystem.API` | `:5003` | OpenIddict AS + ASP.NET Identity (user store) |
| `tms-api` | `TranslationSystem.API` | `:5002` | Resource Server (JwtBearer) |
| Frontend | `Frontend` | `:7017` | OIDC Relying Party (cookie + OIDC) |

Jeden issuer (`auth-api`) obsługuje **przeglądarkę** i **back-channel** Frontendu, więc token `iss`
zgadza się dla obu (CLAUDE.md / ADR-0006). W stacku kontenerowym (`compose.prod.yaml`) API↔API gada
po `http://…:8080`; w dev (ADR-0006 zmieniony przez #190 — wszystkie trzy aplikacje na hoście) ten
back-channel idzie po `https://localhost:5003` (tms-api: fallback `Auth:Authority`→`Auth:Issuer`). W
tokenie i metadanych OIDC `iss` jest zawsze browser-facing (`https://localhost:5003/`).

### 4.2 Klienci OAuth zarejestrowani w bazie
Seedowani na starcie (`DatabaseSeederExtensions.cs:109`), id z `AuthConstants.ClientIds`:

| `client_id` | Typ | Grants | Sekret | Uwagi |
|---|---|---|---|---|
| `lotrokoniecdev-web` | public | auth code+PKCE, refresh | brak | RP; redirect/post-logout URI z konfiguracji |
| `lotrokoniecdev-api` | confidential | client credentials | tak (`OpenIddict:ApiClientSecret`) | service-to-service |
| `lotrokoniecdev-test` | public | password, refresh | brak | **tylko `Testing`** |

---

## 5. OpenIddict — co to i czemu właśnie to

**OpenIddict** to biblioteka do budowy własnego serwera OAuth2/OIDC w ASP.NET Core. Nie hostowany
zewnętrznie (jak Auth0/Entra), tylko **self-hosted** w `auth-api`. Czemu:
- Pełna kontrola nad tokenami, claimami, flow'ami — i zero kosztu/limitu zewnętrznego dostawcy.
- Integruje się z ASP.NET Core Identity (baza userów, hasła, lockout, email confirmation).
- Cały kontekst (Auth) jest **liftem 1:1 z TheKittySaver** (architektoniczna tożsamość repo) —
  sprawdzony, przetestowany serwer.

OpenIddict ma trzy części, wszystkie włączone w `OpenIddictExtensions.cs:17-89`:
- **Core** — EF Core store na aplikacje/tokeny/scope'y (`UseDbContext<AuthDbContext>`).
- **Server** — endpointy `connect/*`, wydawanie/walidacja tokenów, klucze.
- **Validation** (`UseLocalServer`) — walidacja tokenów w samym `auth-api` (np. dla `userinfo`).

---

## 6. AuthSystem — Authorization Server krok po kroku

### 6.1 ASP.NET Core Identity (baza userów)
`AddIdentityCore<ApplicationUser>` (`PersistenceDependencyInjection.cs:39`). `ApplicationUser`
rozszerza `IdentityUser<Guid>` o zgody RODO (`ApplicationUser.cs`). Reguły (`:41-49`):
- hasło: digit + lowercase + uppercase + non-alphanumeric, `RequiredLength = 8`;
- `User.RequireUniqueEmail = true`;
- `SignIn.RequireConfirmedEmail = true` — **bez potwierdzenia maila nie zalogujesz się**;
- lockout: `MaxFailedAccessAttempts = 5`, `DefaultLockoutTimeSpan = 5 min`.

Tokeny email-confirmation/reset żyją 24 h (`DataProtectionTokenProviderOptions`, `:58`).

### 6.2 OpenIddict — konfiguracja serwera
`OpenIddictExtensions.cs:23-84`:
- endpointy: `connect/token`, `connect/authorize`, `connect/userinfo`, `connect/introspect`,
  `connect/revoke`, `connect/logout`;
- flow'y: refresh + client credentials + auth code (+ password tylko w `Testing`);
- `RequireProofKeyForCodeExchange()` — PKCE wymagane;
- `UseReferenceRefreshTokens()` — refresh tokeny **w bazie** (rewokowalne, rolling — §10.2);
- `RegisterScopes(openid, email, profile, roles, offline_access, api, service)`;
- `DisableAccessTokenEncryption()` — access token podpisany, nieszyfrowany (standardowa walidacja JWT).

### 6.3 Klucze kryptograficzne
`OpenIddictExtensions.cs:66-72` + `ConfigureOpenIddictServerSettings` (`:97-195`):
- **Dev/Testing** → `AddEphemeralSigningKey()` + `AddEphemeralEncryptionKey()` — efemeryczne, generowane
  per start (restart = nowe klucze, stare tokeny tracą ważność — i to jest OK lokalnie).
- **Produkcja** → z konfiguracji:
  - **Signing** = RSA (min **2048** bit, walidowane `:149`), publiczna połówka idzie w JWKS;
  - **Encryption** = symetryczny AES (min **256** bit, `:123`) — do auth codes / refresh tokenów,
    nie wystawiany w JWKS;
  - **rotacja**: opcjonalny `PreviousRsaPrivateKeyXml` (`:172-193`) — tokeny podpisane starym kluczem
    zostają ważne w oknie rotacji (`kid` = `signing-key-previous`).

### 6.4 Endpointy OpenIddict
| Endpoint | Plik | Co robi |
|---|---|---|
| `connect/authorize` | `AuthorizeEndpoint.cs` | wejście auth code; challenge cookie Identity, buduje `ClaimsIdentity` z `sub`/`email`/`name`/`role`, `SignIn` |
| `connect/token` | `TokenEndpoint.cs` | wydanie tokenów; rozdziela grant (auth code / refresh / client credentials / password) |
| `connect/userinfo` | `UserInfoEndpoint.cs` | claimy usera wg przyznanych scope'ów (`email`/`profile`/`roles`) |
| `connect/logout` | `LogoutEndpoint.cs` | RP-initiated end-session: **rewokuje reference tokeny** usera, czyści cookie |
| `connect/revoke` | `RevokeEndpoint.cs` | rewokacja pojedynczego tokena |

`TokenEndpoint.cs:147-154` ustawia **destinations** — które claimy lądują w access tokenie, a które w
id tokenie. `sub`/`email`/`name`/`role` idą do obu.

### 6.5 Register endpoint (custom) — i **brak sagi**
`POST auth/register` (`RegisterUser.cs`, `AllowAnonymous`, rate-limited):
1. walidacja (email unikalny/regex ≤250, username ≤150, phone ≤30, hasło 8–128 + złożoność, **obie
   zgody RODO `true`**);
2. `userManager.CreateAsync` → przypisanie roli **`Translator`** (`:140`);
3. wysyłka maila potwierdzającego; gdy mail padnie → **fallback auto-confirm** (`:148-157`);
4. zwraca **201** z gołym `IdentityId`.

**Kluczowe (`RegisterUser.cs:170-173`):** *brak* cross-contextowej sagi `RegisterUser→CreatePerson`
(świadomy non-lift KittySavera). Profil tłumacza w TMS powstaje **leniwie** przy pierwszym
uwierzytelnionym zapisie (§7.3, ADR-0004). To największa różnica od KittySavera — i świetny temat na
rozmowę („dlaczego rozdzieliliście rejestrację od profilu domenowego").

### 6.6 Skąd się biorą roles i scopes w tokenie
- **roles**: z `userManager.GetRolesAsync(user)` przy wydawaniu tokena (`TokenEndpoint.cs:199`,
  `AuthorizeEndpoint.cs:89`), wstawiane jako claimy `role`.
- **scopes**: z requestu klienta (`request.GetScopes()`), ograniczone permissionami klienta seedowanymi
  w bazie. `SetResources(AuthConstants.ClientIds.Api)` ustawia `aud` na `lotrokoniecdev-api`.

Role seedowane (`DatabaseSeederExtensions.cs:37`): **`Admin`** i **`Translator`**.

---

## 7. TranslationSystem — Resource Server + leniwe prowizjonowanie

### 7.1 Konfiguracja JwtBearer
`ApiDependencyInjection.cs:141-187`. Standardowy `AddJwtBearer` (nie OpenIddict validation — prościej):
- `Authority = EffectiveAuthority` (issuer lub osobne Authority dla Dockera);
- `Audience = "lotrokoniecdev-api"`;
- `TokenValidationParameters`: waliduje issuer + audience + lifetime + signing key;
- `NameClaimType = "name"`, `RoleClaimType = "role"`, `MapInboundClaims = false`;
- **klucze z JWKS**: `ConfigurationManager` ciągnie `{authority}/.well-known/openid-configuration`
  (`:183`) — `tms-api` nie zna klucza prywatnego, tylko publiczny przez JWKS.
- `RequireHttpsMetadata` wyłączone w dev/test i dla wewnętrznego `http://`.

### 7.2 Authorization Policies
`ApiDependencyInjection.cs:198-219`:

| Policy | Reguła |
|---|---|
| **fallback** | `RequireAuthenticatedUser()` — domyślnie dla każdego endpointu bez metadanych |
| `RequireAdminRole` | rola `Admin` |
| `RequireTranslatorRole` | rola `Admin` **lub** `Translator` |
| `RequireAuthenticatedUser` | uwierzytelniony |
| `ApiScope` / `RequireServiceScope` | claim `scope` zawiera `api` / `service` (`HasScope`, `:221`) |

Endpointy są **autoryzowane domyślnie** — anonim dostaje 401. Publiczne (download pliku, health) jawnie
`AllowAnonymous`.

### 7.3 Leniwe, idempotentne prowizjonowanie tłumacza (ADR-0004) — nasza specyfika

To serce różnicy od KittySavera. `tms-api` potrzebuje **lokalnej** tożsamości tłumacza (`Translator`),
by renderować „submitted by / approved by <name>" dla *innych* tłumaczy — czego JWT bieżącego usera nie
rozwiąże. Zamiast sagi przy rejestracji: **first-touch w handlerach zapisu**.

`TranslatorProvisioner.ProvisionCurrentAsync` (`TranslatorProvisioner.cs:40`):
1. czyta `IdentityId` z claimu `sub` (`CurrentUserAccessor.cs:27`); brak → `Forbidden`;
2. buduje `DisplayName` z claimu `name` (fallback `email`) + opcjonalny `Email`;
3. **get-or-create** po `IdentityId`: istnieje → `RefreshProfile` (zbiega nazwę po przemianowaniu);
   nie istnieje → `Create` + insert;
4. **wyścig pierwszego zapisu**: `DbUpdateException` (unikalny indeks `Translators.IdentityId`) ⇒ odpina
   swój insert, re-czyta zacommitowany wiersz, refreshuje (`:86-103`).

Wołane jako **pierwszy krok** handlerów zapisu, które stemplują `TranslatorId` — upsert
(`UpsertTranslation.cs:115`) i approve (`ApproveTranslation.cs:86`). Odczyty (lista/detal) **nie**
prowizjonują — join read-modelu rozwiązuje nazwy bez tworzenia wiersza widza.

> Łańcuch referencji: `Translation.SubmittedById/ApprovedById → Translator.Id`, a
> `Translator.IdentityId → user Auth`. Znormalizowany wiersz `Translator` zamiast denormalizowanej
> nazwy na każdym wierszu — przy zmianie nazwy nic nie dryfuje. Szczegóły: [DOMAIN.md §Translator](DOMAIN.md#3-translator-adr-0004).

### 7.4 Użycie w endpointach
```csharp
endpointRouteBuilder.MapPost("/api/v1/translations/{id:guid}/approve", /* … */)
    .RequireAuthorization(AuthorizationPolicies.RequireAdminRole);   // ApproveTranslation.cs:122
```
`ClaimsPrincipal user` w delegacie + `user.IsInRole(AuthConstants.Roles.Admin)` kształtuje też linki
HATEOAS (np. `approve` widoczne tylko dla admina) — `ListTranslations.cs:137`.

---

## 8. Frontend (Blazor SSR) — Client

### 8.1 Schemat: cookies + OIDC
`AuthenticationDependencyInjectionExtensions.cs:55-83`. Dwa schematy:
- **Cookie** (`DefaultScheme`) — sesja usera po zalogowaniu (`.lotrokoniecdev.auth`, HttpOnly, SameSite
  Lax, 8 h sliding);
- **OpenIdConnect** (`DefaultChallengeScheme` + `DefaultSignOutScheme`) — challenge do `auth-api`.

Po zalogowaniu user nosi **cookie**, nie token w przeglądarce; tokeny (access/refresh/id) leżą w
zaszyfrowanej sesji po stronie serwera (`SaveTokens = true`).

### 8.2 Konfiguracja OIDC
`AuthenticationDependencyInjectionExtensions.cs:94-130`:
- `Authority = settings.Authority` (`auth-api`), `ClientId = "lotrokoniecdev-web"`;
- `ResponseType = code`, `UsePkce = true`, `SaveTokens = true`;
- `GetClaimsFromUserInfoEndpoint = true` — dociąga claimy z `connect/userinfo`;
- `MapInboundClaims = false`, `NameClaimType = "name"`, `RoleClaimType = "role"`;
- `CallbackPath` / `SignedOutCallbackPath` z konfiguracji; scope'y z `settings.Scopes`;
- `OnValidatePrincipal = CookieTokenRefresher.ValidateAsync` — odświeża wygasający access token z
  refresh tokena na każdym żądaniu (§8.4);
- `OnRemoteFailure` / `OnAccessDenied` → dedykowane strony błędów (trace ID zachowany).

### 8.3 Login / logout flow
`AuthEndpointsExtensions.cs`:
- **Login**: `GET /auth/login?returnUrl=…` → `Results.Challenge` na schemat OIDC → 302 do
  `connect/authorize`. `returnUrl` waliduje `IsLocalUrl` (anti open-redirect).
- **Logout**: `POST /auth/logout` → `SignOutAsync(cookie)` + redirect na `connect/logout` z
  `id_token_hint` + `post_logout_redirect_uri` (RP-initiated end-session). `auth-api` przy logoucie
  **rewokuje reference tokeny** usera.

### 8.4 CookieTokenRefresher
`Infrastructure/Auth/TokenRefresh/CookieTokenRefresher.cs` na `OnValidatePrincipal`: gdy access token
bliski wygaśnięcia, wymienia refresh token na nowy w `connect/token` (przez `ITokenEndpointClient`) i
aktualizuje sesję. Rolling refresh ⇒ stary refresh token unieważniony, nowy zapisany. Martwa sesja
(refresh odrzucony) ⇒ `DeadSessionRegistry` (czysty re-login).

### 8.5 Wywołania do API z tokenem
`TranslationContentNegotiationAndAuthDelegatingHandler` dokłada `Authorization: Bearer <access>` (z
sesji) + nagłówek `Accept` HATEOAS do każdego wywołania `tms-api`. Frontend referuje
`TranslationSystem.Contracts` bezpośrednio (te same DTO).

---

## 9. Service-to-service (client credentials)

Gdy `auth-api` musi zawołać `tms-api` bez usera (albo dowolna usługa-do-usługi): grant
**client_credentials** klienta `lotrokoniecdev-api` (confidential, sekret). `TokenEndpoint.cs:161-183`
buduje `ClaimsIdentity` z `sub = client_id`, scope'ami z requestu i `aud = lotrokoniecdev-api`. Po
stronie `tms-api` taki token przechodzi policy `RequireServiceScope` (scope `service`) — gdyby slice
tego wymagał (dziś żaden nie wymaga, ale infrastruktura jest gotowa).

---

## 10. Bezpieczeństwo — co i czemu

### 10.1 PKCE
`RequireProofKeyForCodeExchange()` (`OpenIddictExtensions.cs:45`). Klient publiczny (Frontend, bez
sekretu) generuje `code_verifier`, wysyła `code_challenge = SHA256(verifier)` przy `authorize`, a przy
wymianie `code → token` dowodzi posiadania `verifier`. Chroni przed przechwyceniem kodu autoryzacyjnego.

### 10.2 Rolling reference refresh tokens
`UseReferenceRefreshTokens()` (`:50`). Refresh tokeny są **referencyjne** (zapisane w bazie, nie
self-contained) ⇒ **rewokowalne**. Rolling: użycie refresh tokena unieważnia stary i wydaje nowy ⇒
ogranicza replay. Logout rewokuje wszystkie (`LogoutEndpoint.cs:25-28`).

### 10.3 Token revocation przy logout
`LogoutEndpoint.cs`: `tokenManager.FindBySubjectAsync(userId)` → `TryRevokeAsync` dla każdego, potem
`SignOutAsync`. Po logoucie żaden refresh token usera nie zadziała.

### 10.4 Timing attack mitigation
`TokenEndpoint.cs:20`, `:85-87`: gdy user nie istnieje, i tak liczony jest **dummy hash**, by czas
odpowiedzi nie zdradzał „user not found" vs „złe hasło" (anti-enumeration). Forgot/Resend password
zawsze zwracają sukces (ten sam wzorzec).

### 10.5 HTTPS i metadata
Produkcja: `RequireHttpsMetadata = true` (`ApiDependencyInjection.cs:158`), JWKS/discovery tylko po
HTTPS. Dev/test i wewnętrzne `http://…:8080` (compose) wyłączają wymóg.

### 10.6 Cookie security
`.lotrokoniecdev.auth`: `HttpOnly` (JS nie czyta), `SameSite=Lax` (anti-CSRF), `SecurePolicy`
zależny od żądania, `Path=/` (sign-in/out zawsze trafiają w to samo cookie). 8 h sliding.

### 10.7 Wymaganie zgody RODO przy rejestracji
`RegisterUser.cs:55-59`: `acceptedPrivacyPolicy` i `acceptedDataProcessingConsent` **muszą być `true`**,
data zgody stemplowana na `ApplicationUser`. `DeleteAccount` = erasure RODO + permanentny lockout;
`auth/account/data-export` = eksport danych.

### 10.8 Walidacja kluczy w produkcji
`ConfigureOpenIddictServerSettings`: RSA min 2048 bit, AES min 256 bit, czytelne wyjątki przy złym
formacie base64/XML (`OpenIddictExtensions.cs:123`, `:149`). `OpenIddictSettingsValidator` egzekwuje
ustawienia tylko w produkcji.

---

## 11. Jak to opowiadać na rozmowie — cheatsheet

### Wersja 30-sekundowa
> „Mam self-hosted OAuth2/OIDC oparty o OpenIddict + ASP.NET Identity (`auth-api`). Frontend Blazor SSR
> to OIDC relying party — loguje usera przez authorization code + PKCE, trzyma tokeny w sesji za cookie.
> API translacji (`tms-api`) to resource server: waliduje podpisany JWT przez JWKS i autoryzuje po
> rolach. Profil tłumacza w domenie powstaje **leniwie** przy pierwszym zapisie, nie sagą przy
> rejestracji — czysty rozdział bounded contextów."

### Wersja 2-minutowa
Dorzuć: reference rolling refresh tokeny (rewokacja przy logoucie), `MapInboundClaims=false` (surowe
claimy OpenIddict), efemeryczne klucze w dev / RSA z rotacją w prod, fallback policy = authorized by
default, role `Admin`/`Translator`, i czemu lazy provisioning (read path renderuje cudze nazwy → join
read-modelu, write path stempluje FK → first-touch get-or-create idempotentny na unikalnym indeksie).

### Skróty, które trzeba znać
AS / RS / RP · JWT (`header.payload.signature`) · JWKS · `sub`/`aud`/`iss`/`exp` · PKCE · scope vs role
· auth code vs client credentials · reference vs self-contained token · rolling refresh.

---

## 12. Pytania, które dostaniesz na 100%

**Q: Czemu JWT a nie sesja w bazie?** — RS waliduje stateless (podpis + JWKS + exp), bez round-tripu do
AS. Skalowalne. Koszt: trudna rewokacja access tokena ⇒ krótki żywot + rewokowalne refresh tokeny.

**Q: Co to PKCE i czemu potrzebne?** — Klient publiczny nie ma sekretu; PKCE (`code_verifier` +
`code_challenge`) dowodzi, że ten sam klient, który zaczął flow, wymienia kod ⇒ przechwycony kod jest
bezużyteczny.

**Q: Jak waliduje się token po stronie API?** — `JwtBearer`: ściąga JWKS z
`{authority}/.well-known/openid-configuration`, sprawdza podpis, `iss`, `aud`, `exp`. Klucza prywatnego
RS nie zna.

**Q: Co się dzieje przy logoucie?** — Cookie sign-out + RP-initiated end-session do `auth-api`, które
**rewokuje wszystkie reference tokeny** usera. Access token (krótki) wygaśnie sam.

**Q: Czemu refresh tokeny są referencyjne?** — Żeby były **rewokowalne** (w bazie) i rolling (replay
mitigation). Self-contained nie da się unieważnić przed wygaśnięciem.

**Q: Czemu OpenIddict a nie gotowy IdP?** — Pełna kontrola, zero zależności/kosztu zewnętrznego, lift
1:1 z referencyjnego TheKittySaver. (Sam KittySaver migruje docelowo do Entra — u nas OpenIddict
zostaje na obecnym etapie.)

**Q: Jak działa service-to-service?** — Grant client_credentials klienta `lotrokoniecdev-api`
(confidential, sekret); token bez usera, `sub = client_id`, autoryzacja po scope `service`.

**Q: Co to scopes i czym różnią się od ról?** — Scope = na co klient prosi o pozwolenie (`api`,
`email`); rola = uprawnienie usera (`Admin`/`Translator`). Scope filtruje *co klient* widzi, rola *co
user* może.

**Q: Co jak ktoś zmieni payload JWT?** — Podpis przestaje pasować, `JwtBearer` odrzuca (401). Bez
klucza prywatnego nie da się przepodpisać.

**Q: Co to lazy provisioning i czemu zamiast sagi?** — Rejestracja tworzy tylko usera Auth; lokalny
`Translator` w TMS powstaje przy pierwszym zapisie (get-or-create idempotentny po `IdentityId`,
unikalny indeks chroni przed wyścigiem). Zero runtime coupling read-path → Auth; bounded contexty
rozdzielone (ADR-0004).

**Q: Co jak AuthSystem padnie?** — Nowe logowania/refreshe padają, ale już-wydane access tokeny działają
do `exp` (stateless). `tms-api` cache'uje JWKS, więc walidacja istniejących tokenów przeżywa krótką
niedostępność `auth-api`.

---

## 13. Pliki do otwarcia, gdy zapytają „pokaż mi kod"

| Temat | Plik |
|---|---|
| OpenIddict server config | `AuthSystem.API/Extensions/OpenIddictExtensions.cs` |
| Wydawanie tokenów (granty) | `AuthSystem.API/Features/Auth/TokenEndpoint.cs` |
| Auth code entry | `AuthSystem.API/Features/Auth/AuthorizeEndpoint.cs` |
| Logout + rewokacja | `AuthSystem.API/Features/Auth/LogoutEndpoint.cs` |
| Rejestracja (bez sagi) | `AuthSystem.API/Features/Auth/RegisterUser.cs` |
| Seed klientów/ról/scope'ów | `AuthSystem.API/Extensions/DatabaseSeederExtensions.cs` |
| Identity (hasło/lockout/email) | `AuthSystem.Persistence/PersistenceDependencyInjection.cs` |
| JwtBearer + policies (RS) | `TranslationSystem.API/ApiDependencyInjection.cs` |
| Leniwe prowizjonowanie | `TranslationSystem.API/Auth/Provisioning/TranslatorProvisioner.cs` |
| Odczyt tożsamości z tokena | `TranslationSystem.API/Auth/CurrentUserAccessing/CurrentUserAccessor.cs` |
| OIDC RP (cookie + OIDC) | `Frontend/Infrastructure/Auth/AuthenticationDependencyInjectionExtensions.cs` |
| Login/logout RP | `Frontend/Infrastructure/Auth/AuthEndpointsExtensions.cs` |
| Refresh tokena na froncie | `Frontend/Infrastructure/Auth/TokenRefresh/CookieTokenRefresher.cs` |

Powiązane: [API.md](API.md) (kontrakty HTTP), [INVARIANTS.md §11](INVARIANTS.md#11-authsystem--uwierzytelnianie)
(reguły auth), [DOMAIN.md §Translator](DOMAIN.md#3-translator-adr-0004) (model tożsamości tłumacza),
ADR-0002 §6-7 (auth od dnia 1, brak sagi) + ADR-0004 (agregat `Translator`).
