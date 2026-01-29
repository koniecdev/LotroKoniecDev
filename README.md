# LOTRO Polish Patcher

Narzedzie do wstrzykiwania polskich tlumaczen do plikow DAT gry Lord of the Rings Online.

## Wymagania

- Windows (x86/x64)
- [.NET 10 Runtime x86](https://dotnet.microsoft.com/download/dotnet/10.0) (sam runtime, nie SDK)
- Plik `client_local_English.dat` z instalacji LOTRO

## Szybki start

### Dla deweloperow (z kodem zrodlowym)

1. Zainstaluj [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
2. Skopiuj `client_local_English.dat` z katalogu gry do `data/`
3. Umiesc plik tlumaczen w `translations/` (np. `translations/polish.txt`)
4. Odpal:

```
patch.bat polish
```

### Dla uzytkownikow (samo exe)

1. Zainstaluj [.NET 10 Runtime x86](https://dotnet.microsoft.com/download/dotnet/10.0)
2. Pobierz cala zawartosc katalogu `bin/Debug/net10.0/` (exe + wszystkie DLL)
3. Umiesc `client_local_English.dat` w katalogu `data/` obok exe
4. Umiesc plik tlumaczen w katalogu `translations/`
5. Odpal z konsoli:

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
patch.bat polish             ->  translations/polish.txt
```

Mozna tez podac pelna sciezke:

```
patch.bat translations/polish.txt
patch.bat C:\moje_tlumaczenia\quest1.txt
```

Przed patchowaniem automatycznie tworzy backup pliku DAT (`client_local_English.dat.backup`).

### Eksport tekstow z gry

```
export.bat
```

Eksportuje wszystkie teksty z `data/client_local_English.dat` do `data/exported.txt`. Przydatne jako baza do tlumaczenia.

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
  data/                         # Pliki DAT gry
    client_local_English.dat    # <- skopiuj z katalogu LOTRO
    exported.txt                # <- generowany przez export
  translations/                 # Pliki tlumaczen
    example_polish.txt          # Przyklad
  src/
    LotroKoniecDev/             # CLI (punkt wejscia)
    LotroKoniecDev.Application/ # Logika biznesowa
    LotroKoniecDev.Domain/      # Model domenowy
    LotroKoniecDev.Infrastructure/ # Obsluga plikow DAT (natywne DLL)
  patch.bat                     # Skrot: buduj + patchuj
  export.bat                    # Skrot: buduj + eksportuj
```

## Gdzie znalezc client_local_English.dat

Domyslna sciezka instalacji LOTRO:

```
C:\Program Files (x86)\StandingStoneGames\The Lord of the Rings Online\client_local_English.dat
```

## Przywracanie oryginalu

Jesli cos poszlo nie tak, backup jest w `data/client_local_English.dat.backup`. Wystarczy skopiowac go z powrotem na `client_local_English.dat`.
