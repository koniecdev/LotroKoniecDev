# LOTRO Polish Patcher

Narzedzie do wstrzykiwania polskich tlumaczen do plikow DAT gry Lord of the Rings Online.

**Status:** CLI (`export` / `patch` / `launch`) dziala i jest przetestowane na zywych
aktualizacjach gry. W budowie: webowa platforma do zarzadzania tlumaczeniami
(Web API + Blazor SSR + PostgreSQL + OpenIddict).

## Wymagania

- Windows (x86/x64)
- [.NET 10 Runtime x86](https://dotnet.microsoft.com/download/dotnet/10.0) (sam runtime, nie SDK)
- Zainstalowane LOTRO

## Szybki start

### Dla deweloperow (z kodem zrodlowym)

1. Zainstaluj [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
2. Umiesc plik tlumaczen w `translations/` (np. `translations/polish.txt`)
3. Odpal:

```
patch.bat polish
```

Patcher automatycznie znajdzie instalacje LOTRO i spatchuje plik DAT.

### Dla uzytkownikow (samo exe)

1. Zainstaluj [.NET 10 Runtime x86](https://dotnet.microsoft.com/download/dotnet/10.0)
2. Pobierz cala zawartosc katalogu `bin/Debug/net10.0-windows/` (exe + wszystkie DLL)
3. Umiesc plik tlumaczen w katalogu `translations/`
4. Odpal z konsoli:

```
LotroKoniecDev.exe patch polish
```

## Komendy

### Patchowanie (wstrzykiwanie tlumaczen)

```
patch.bat <nazwa>
```

`<nazwa>` to nazwa pliku w `translations/` bez rozszerzenia `.txt`:

```
patch.bat example_polish    ->  translations/example_polish.txt
patch.bat polish            ->  translations/polish.txt
```

Mozna tez podac pelna sciezke do tlumaczenia i/lub do pliku DAT:

```
patch.bat polish C:\sciezka\do\client_local_English.dat
patch.bat C:\moje_tlumaczenia\quest1.txt
```

### Auto-discovery instalacji LOTRO

Jesli nie podasz sciezki do pliku DAT, patcher automatycznie szuka instalacji LOTRO:

1. Domyslna sciezka SSG: `C:\Program Files (x86)\StandingStoneGames\The Lord of the Rings Online\`
2. Steam: `C:\Program Files (x86)\Steam\steamapps\common\The Lord of the Rings Online\`
3. Rejestr Windows (klucze StandingStoneGames / Turbine)
4. Full scan dyskow (jesli nic nie znaleziono wyzej)
5. Lokalne `data/client_local_English.dat` (fallback)

Jesli znajdzie wiele instalacji (np. Live + Bullroarer), zapyta ktora wybrac.

### Pre-flight checks

Przed patchowaniem automatycznie:
- Sprawdza czy LOTRO jest uruchomione (plik DAT moze byc zablokowany)
- Sprawdza uprawnienia do zapisu (Program Files wymaga admina)
- Tworzy backup pliku DAT (`.backup`)

### Eksport tekstow z gry

```
export.bat
```

Eksportuje wszystkie teksty z pliku DAT do `data/exported.txt`. Przydatne jako baza do tlumaczenia.

### Launch (patch + uruchomienie gry)

```
LotroKoniecDev.exe launch polish
```

Sprawdza hash pliku tlumaczen, patchuje tylko jesli tlumaczenia sie zmienily, po czym uruchamia
launcher LOTRO. Testy na zywo wykazaly, ze tlumaczenia przezywaja aktualizacje gry — to zalecany
sposob codziennego grania.

### lotro.bat (alternatywa manualna)

```
lotro.bat
```

Starszy helper: ustawia plik DAT na read-only, uruchamia launcher LOTRO, a po zamknieciu gry
przywraca zapis. Ochrona read-only okazala sie w testach zbedna (tlumaczenia przezywaja
aktualizacje bez niej) — preferuj komende `launch`.

Mozna podac sciezke do instalacji: `lotro.bat "D:\Games\LOTRO"`

## Format pliku tlumaczen

Pliki `.txt` w katalogu `translations/`. Kazda linia to jedno tlumaczenie:

```
file_id||gossip_id||przetlumaczony_tekst||args_order||args_id||approved
```

Przyklady:

```
# Prosty tekst (bez argumentow):
620756992||1001||Witaj w Srodziemiu!||NULL||NULL||1

# Tekst z argumentem (np. imie gracza):
620756992||1002||Witaj, <--DO_NOT_TOUCH!-->!||1||1||1

# Tekst z wieloma argumentami:
620756992||1003||Masz <--DO_NOT_TOUCH!--> zlota i <--DO_NOT_TOUCH!--> srebra.||1-2||1-2||1

# Zmieniona kolejnosc argumentow (oryg: "Level {0}: {1}" -> "Poziom {1}: {0}"):
620756992||1004||Poziom <--DO_NOT_TOUCH!-->: <--DO_NOT_TOUCH!-->||2-1||1-2||1
```

Zasady:
- Linie zaczynajace sie od `#` sa ignorowane (komentarze)
- Puste linie sa ignorowane
- `<--DO_NOT_TOUCH!-->` to placeholdery na argumenty gry - nie zmieniaj ich
- `args_order` - kolejnosc argumentow w tlumaczeniu (np. `2-1` zamienia kolejnosc)
- `args_id` - ID argumentow z oryginalu
- `approved` - `1` = zatwierdzone

## Struktura projektu

```
LotroKoniecDev/
  translations/                    # Pliki tlumaczen
    example_polish.txt             # Przyklad
  data/                            # Lokalna kopia DAT (fallback)
  src/
    LotroKoniecDev.Cli/            # CLI (punkt wejscia)
    LotroKoniecDev.Application/    # Use case'y (slim handlery)
    LotroKoniecDev.Domain/         # Model domenowy, Result
    LotroKoniecDev.Infrastructure/ # Obsluga plikow DAT (natywne DLL)
    LotroKoniecDev.Primitives/     # Stale i enumy
  tests/                           # Unit / Infrastructure / E2E
  patch.bat / export.bat / lotro.bat
```

## Przywracanie oryginalu

Backup pliku DAT jest tworzony automatycznie z rozszerzeniem `.backup` obok oryginalu. Aby przywrocic oryginal, skopiuj backup z powrotem na `client_local_English.dat`.

## Dokumentacja (TMS)

Cztery dokumenty referencyjne dla backendu TMS — wygenerowane **z kodu** (kod jest źródłem prawdy):

- [`docs/API.md`](docs/API.md) — pełna referencja HTTP API: każdy endpoint (`/api/v1/...`), polityki
  autoryzacji, kształty request/response, kody statusu + `ProblemDetails`, endpointy tokenowe Auth i
  dystrybucja pliku tłumaczeń (ETag/304).
- [`docs/DOMAIN.md`](docs/DOMAIN.md) — spacer po modelu domenowym: agregaty (`Translation`,
  `GameVersion`, `Translator`), value objecty, cykl aktualizacji / unieważnienia (spec 0001) i podział
  CQRS read/write.
- [`docs/INVARIANTS.md`](docs/INVARIANTS.md) (+ [`INVARIANTS.slim.md`](docs/INVARIANTS.slim.md)) —
  katalog egzekwowanych reguł z Domain + walidatorów, każda z tagiem 🟢 Domena / 🔵 Aplikacja i kotwicą
  `plik:linia`.
- [`docs/auth-tutorial.md`](docs/auth-tutorial.md) — auth end-to-end: serwer autoryzacji OpenIddict
  (AuthSystem), resource server JwtBearer (tms-api), JWKS, leniwe prowizjonowanie tłumacza (ADR-0004),
  role/policies.

Wdrożenie i operacje: [`docs/deployment/runbook.md`](docs/deployment/runbook.md) — runbook operatora:
macierz zmiennych środowiskowych (usługa × środowisko), generowanie sekretów, reguły spójności
(issuer / redirect / authority / CORS), sekwencja uruchomienia i migracje bazy.

Głębiej: `docs/specs/` (spec 0001 — cykl aktualizacji; spec 0002 — HATEOAS), `docs/adr/` (decyzje
architektoniczne), `docs/knowledge-base/` (empiryczne ustalenia o DAT/aktualizacjach).

## Bezpieczenstwo i sekrety (backend / TMS)

Repo bedzie publiczne — **realne sekrety nigdy nie trafiaja do historii gita**. Trzy warstwy ochrony:

1. **pre-commit (gitleaks)** — blokuje commit z sekretem lokalnie. Setup raz na klon:
   ```
   pip install pre-commit      # lub: brew install pre-commit
   pre-commit install
   ```
   Od teraz `git commit` skanuje staged changes (gitleaks). Konfiguracja: `.pre-commit-config.yaml` + `.gitleaks.toml`.
2. **CI** (`.github/workflows/gitleaks.yml`) — skanuje kazdy PR i push do `main`; PR z sekretem nie przejdzie.
3. **GitGuardian app** — serwerowy skan PR (warstwa dodatkowa).

**Sekrety w dev:** uzywaj `dotnet user-secrets` (per-projekt) albo `.env` (git-ignored, bootstrapowany przez
`scripts/up.sh` z `.env.example`). W `.gitignore` sa `*.env` / `.env.*` (poza `.env.example`),
`appsettings.*.local.json` i `**/secrets.json`. `appsettings.Development.json` trzyma **wylacznie** wartosci dev,
nigdy realne sekrety.

Przyklad ustawienia sekretu w dev przez user-secrets:
```
dotnet user-secrets set "OpenIddict:ApiClientSecret" "<twoj-sekret>" \
  --project src/AuthSystem/LotroKoniecDev.AuthSystem.API
```
