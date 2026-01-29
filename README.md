# LOTRO Polish Patcher

Narzedzie do wstrzykiwania polskich tlumaczen do plikow DAT gry Lord of the Rings Online.

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

### Uruchamianie LOTRO po patchowaniu

```
lotro.bat
```

Launcher LOTRO moze nadpisac spatchowany plik DAT przy aktualizacji. Skrypt `lotro.bat`:
1. Ustawia plik DAT na read-only (chroni tlumaczenia przed nadpisaniem)
2. Uruchamia launcher LOTRO
3. Po zamknieciu gry przywraca zapis do pliku DAT

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
  translations/                 # Pliki tlumaczen
    example_polish.txt          # Przyklad
  data/                         # Lokalna kopia DAT (fallback)
  src/
    LotroKoniecDev/             # CLI (punkt wejscia)
    LotroKoniecDev.Application/ # Logika biznesowa
    LotroKoniecDev.Domain/      # Model domenowy
    LotroKoniecDev.Infrastructure/ # Obsluga plikow DAT (natywne DLL)
  patch.bat                     # Buduj + patchuj
  export.bat                    # Buduj + eksportuj
  lotro.bat                     # Uruchom LOTRO z ochrona DAT
```

## Przywracanie oryginalu

Backup pliku DAT jest tworzony automatycznie z rozszerzeniem `.backup` obok oryginalu. Aby przywrocic oryginal, skopiuj backup z powrotem na `client_local_English.dat`.
