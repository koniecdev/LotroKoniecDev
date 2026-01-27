# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Cel projektu

Ekosystem do tłumaczenia questów w Lord of the Rings Online na język polski, służący jednocześnie jako narzędzie do nauki angielskiego poprzez tłumaczenie i syntezę tekstu.

## Komponenty

### 1. Plugin LOTRO (`src/plugin/`) - Lua 5.1
- Okno modalne z wyszukiwarką po angielskim tytule questa
- Dwa tryby: ręczne wyszukiwanie + auto-popup (nasłuchuje chatu po przyjęciu questa)
- Wyświetla: polski tytuł, streszczenie fabularne, pełny opis (rozwijany), cele z checkboxami
- Dane w tablicy Lua (klucz: angielski tytuł)

### 2. System zarządzania tłumaczeniami (`src/app/`) - .NET 10 Blazor
- **Faza 1 (MVP):** Google Sheets + prosty skrypt eksportu do Lua
- **Faza 2:** Aplikacja .NET 10 z CRUD + integracja AI do review tłumaczeń

## Build and Run Commands

### Lua (Docker)
```bash
docker compose build                          # Zbuduj obraz
docker compose run --rm lua                   # Interaktywny REPL
docker compose run --rm lua lua5.1 Main.lua   # Odpal skrypt
```

### .NET (Faza 2)
```bash
dotnet build src/app/LotroKoniecDevApp/LotroKoniecDevApp/LotroKoniecDevApp.csproj
dotnet run --project src/app/LotroKoniecDevApp/LotroKoniecDevApp/
```

## Struktura danych

### W CRUD / Google Sheets (robocza, pełna):
```
| title_en | title_pl | description_en | description_pl | summary_pl | objectives_en | objectives_pl | ai_review_notes |
```

### Eksport do Lua (tylko polskie dane):
```lua
QuestData = {
    ["English Quest Title"] = {
        title = "Polski tytuł",
        summary = "Krótkie streszczenie fabularne...",
        description = "Pełny opis po polsku...",
        objectives = { "Cel 1", "Cel 2" }
    }
}
```

## Ograniczenia LOTRO API
- Quest data NIE jest bezpośrednio dostępne w Lua API
- Można parsować chat messages po przyjęciu questa ("You have accepted: Quest Name")
- Nie można wykryć otwarcia okna questa u NPC PRZED przyjęciem
- Checkboxy: `Turbine.UI.Lotro.CheckBox`

## Fazy rozwoju

1. **Faza 1 (teraz):** Google Sheets + skrypt eksportu + MVP pluginu (ręczne wyszukiwanie)
2. **Faza 1.5:** Auto-popup po przyjęciu questa (chat listener)
3. **Faza 2:** Checkboxy celów + aplikacja .NET z CRUD i AI review
4. **Faza 2.5:** Auto-odhaczanie celów (parsowanie chatu)

## Styl kodu

### C#
- Wszystkie klasy `sealed` (chyba że jawne dziedziczenie)
- Bez oczywistych komentarzy
- Jeden artefakt per namespace (Rider rozdzieli na pliki)
- Naśladuj styl kodu który użytkownik daje jako przykład

### Lua
- Lokalne zmienne (`local`)
- Czytelne nazewnictwo (angielskie)
- Komentarze tylko gdzie nieoczywiste

## Terminologia LOTRO (spójność)
- Quest = Quest (nie "zadanie") - ingame task dla gracza.
- Fellowship = Drużyna
- Kinship = Gildia w grze
- Middle-earth = Śródziemie
- Shire = Shire

## AI Review tłumaczeń

Gdy użytkownik wrzuca angielski tekst + swoje tłumaczenie, odpowiadaj w formacie:

```
### Review tłumaczenia

**Ogólna ocena:** [X/10]

**Co jest dobrze:**
- ...

**Sugestie poprawek:**
- "oryginał" → "propozycja" (powód)

**Streszczenie fabularne:**
[2-3 zdania podsumowujące questa]
```

Zachowuj klimat fantasy/Tolkienowski i pilnuj spójności terminologii.

## Język komunikacji

Odpowiadaj po polsku (chyba że użytkownik pisze po angielsku).

## Podejście

- Nie generuj kodu bez prośby
- KISS - najprostsze rozwiązania
- Iteracyjnie: najpierw content, potem tooling
