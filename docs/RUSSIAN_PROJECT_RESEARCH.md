# Badanie projektu rosyjskiej rusyfikacji LOTRO (translate.lotros.ru)

Badanie przeprowadzone: 2026-02-09
Zrodla: GitHub, git.endevir.ru, Steam Community, translate.lotros.ru, lotro-mindon.ru, pikabu.ru

---

## 1. Przeglad projektu

**Nazwa:** LOTRO - System Kolektywnego Przekladu (Система коллективного перевода)
**Strona:** http://translate.lotros.ru/
**Cel:** Pelna rusyfikacja LOTRO (Lord of the Rings Online) — tekst, czcionki, obrazy, filmy, ekrany ladowania
**Status:** Aktywny, osiagneli 100% tlumaczenia tekstow, stale aktualizuja pod nowe update'y gry
**Czas dzialania:** 8+ lat (od ~2016-2017)
**Model:** Community-driven, wolontariusze, darmowy

---

## 2. Ewolucja narzedzi (timeline)

```
~2016   Poczatek projektu translate.lotros.ru
        Kubera tworzy Elanor (C#) — pierwszy launcher/patcher

~2017   LOTRO Enhanced Text Patcher v1.09 (Python 2.7 + WxPython)
        Autor: Endevir (Ivan Arkhipov)
        Integracja z translate.lotros.ru API (login, pobieranie tlumaczen)

~2017   Elanor przechodzi do Gi1dor i Coder
        6 commitow, GPL-3.0

~2018   Legacy (Наследие) v1 — launcher lokalizacji
        C++ z Qt 5.9, bazuje na LotroDat library

2018    Legacy v2 — poprawiony GUI, nowe features
        Standalone installer, ikona na pulpicie
        Modularnosc: tekst, font, obraz, dzwiek, tekstura, ekran ladowania, film
        ~1.5 GB pelna lokalizacja z filmami

2023    Legacy (Наследие) 3.0 — aktualna wersja
        Auto-updater (loader module sprawdza integralnosc plikow)
        System mikro-aktualizacji (raz dziennie najnowsze tlumaczenia)
        Wsparcie Linux (Proton na Steam Deck)
        Zabezpieczenie procesu patchowania (lock przy instalacji)
```

---

## 3. Architektura — komponenty

### 3.1 Web Platform (translate.lotros.ru)

**Typ:** Platforma kolektywnego tlumaczenia (custom-built)
**Funkcje:**
- Rejestracja i logowanie uzytkownikow
- System rol: translator, moderator, developer, administrator
- Przegladanie i edycja tlumaczen
- System zatwierdzania (approved = 0/1)
- API dla narzedzi desktopowych
- Bug tracker (zglaszanie bledow)
- Strony z FAQ, gajdami
- System newsow i ogloszen

**API Endpoints (odtworzone z kodu Python patchera):**

```
POST /auth/autologin?logout=1&email={email}&password={password}
  Response: "null" (blad) | "GROUPNAME||NICKNAME" (sukces)

GET /groupware/get/{timestamp}?to={timestamp}&translator={nick}&success={0|1}
  Response: file_id||gossip_id||content||args_order||args_id||approved\r\n (wiele linii)

GET /groupware/get/{timestamp}?to={timestamp}&moder={nick}
  Response: j.w. (texty zatwierdzone przez moderatora)

GET /groupware/getitem/{file_id}/{gossip_id}
  Response: file_id||gossip_id||content||args_order||args_id (jeden string)

GET /pages/easypatcher.html
  Response: HTML z tagiem <version>X.XX</version> (sprawdzanie wersji)
```

**Format danych API:**
```
file_id||gossip_id||content||args_order||args_id||approved
```
Identyczny z naszym formatem plikow tlumaczen!

### 3.2 Enhanced Text Patcher (starszy Python tool)

**Technologia:** Python 2.7, WxPython (GUI), PyInstaller (packaging)
**Repozytorium:** https://github.com/Endevir/LOTRO-Enchanced-text-patcher
**Wersja:** v1.09 (2017, MIT License)
**Pliki:**
```
main.py          - Entry point, config, inicjalizacja
Frame.py         - Glowne okno, polaczenie z serwerem, launch gry
MainGUI.py       - Backup/restore, logi, FAQ
LoginGUI.py      - Autentykacja z translate.lotros.ru
UpdateGUI.py     - Pobieranie tlumaczen, tworzenie patchy
patchimari.py    - Core patching logic (SQLite -> DAT)
datexport.py     - Wrapper P/Invoke dla datexport.dll
ProgressDialog.py - Dialog postepu
SelfPatchGUI.py  - Self-update
```

**Kluczowy workflow:**
1. Login do translate.lotros.ru (email + password -> gruppa + nick)
2. Wybor zakresu: ostatnia godzina / dzien / tydzien / custom daty
3. GET /groupware/get/{from}?to={to}&translator={nick}&success={0|1}
4. Parsowanie odpowiedzi (pipe-delimited, identyczny format jak nasz)
5. Tworzenie lokalnego SQLite patcha (tabela text_files)
6. Wywolanie PATCH_IT() — otwarcie DAT, wstrzykniecie tekstu

**Konfiguracja (APPDATA/Cenchanced/config.cc):**
```
Version=1.09
DatPath=C:\...\client_local_English.dat
UserName=email@example.com
UserNick=NickName
LoginTime=1234567890
UserGroup=translator
OpenFAQ=True
LastUpdateTime=1000000
ForceSaveLogin=False
```

**Launch gry z Python patchera:**
```python
execstr = '"' + dat_path[:-24] + 'TurbineLauncher.exe" -nosplash -disablePatch -skiprawdownload'
win32api.WinExec(execstr)
```

**Flagi launchera:**
- `-nosplash` — pomijanie splash screena
- `-disablePatch` — KLUCZOWE: blokuje oficjalny patcher gry
- `-skiprawdownload` — pomijanie pobierania zasobow RAW

### 3.3 Elanor Launcher (pierwszy C# launcher)

**Technologia:** C# 100%, Visual Studio
**Repozytorium:** https://github.com/Endevir/Elanor (GPL-3.0)
**Status:** Legacy, 6 commitow
**Moduly:**
```
elanor/     - glowny launcher
jozo/       - komponent pomocniczy
mojo/       - konwerter czcionek (font converter!)
shared/     - wspolne biblioteki
xavian/     - dodatkowa funkcjonalnosc
```

**Autor oryginalny:** Kubera
**Utrzymanie:** Gi1dor, Coder

### 3.4 LotroDat Library (core C++ library)

**Technologia:** C++ standalone library
**Repozytorium:** https://git.endevir.ru/LotRO_Legacy/LotroDat
**Cel:** Pelna obsluga plikow .dat LOTRO (extract, manage, emplace)
**Kluczowy plik:** Subfile.h — definicje typow podplikow

Biblioteka ta ZASTEPUJE datexport.dll — Rosjanie stworzyli wlasna implementacje
od zera zamiast polegac na zamknietej bibliotece Turbine!

### 3.5 Legacy Launcher v2 (C++ z Qt)

**Technologia:** C++ z Qt 5.9
**Repozytorium:** https://git.endevir.ru/LotRO_Legacy/Legacy_v2
**Zaleznosci:** LotroDat, yaml-cpp, zlib, Easylogging++, SQLite
**Status:** Deprecated (zastapiony przez Legacy 3.0)

**Features:**
- Standalone installer, ikona na pulpicie
- Wybor komponentow lokalizacji (toggle on/off):
  - Tekst (text)
  - Czcionka (font) — wlasna czcionka Cyrillic
  - Obrazy (image) — przetlumaczone UI/mapy
  - Dzwiek (sound) — rosyjskie voiceover
  - Tekstury (texture)
  - Ekrany ladowania (loadscreen)
  - Filmy (video) — przetlumaczone cutscenki
- Automatyczne sledzenie zmian w plikach lokalizacji
- Re-aplikacja patchy przy wykryciu zmian
- Bezpieczne launchowanie przez Steam

### 3.6 Legacy (Наследие) 3.0 (aktualna wersja)

**Status:** Aktywny, pobierany z translate.lotros.ru
**Platformy:** Windows (primary), Linux (Proton/Wine), Steam Deck

**Features:**
- Loader module: sprawdza integralnosc plikow Наследие przy kazdym uruchomieniu
- Auto-update: automatyczne aktualizacje programu (zero recznego sledzenia)
- Mikro-aktualizacje: raz dziennie najnowsze tlumaczenia (poza oficjalnymi patchami)
- Lock przy patchowaniu: blokada zamkniecia aby nie uszkodzic pliku DAT
- Wsparcie Linux: kompatybilne z Proton 5.13-6, 6.3-8, GE-Proton9
- ~700 MB rozmiar pelnej lokalizacji

### 3.7 FontRestorator

**Technologia:** C++ (Gi1dor)
**Repozytorium:** https://git.endevir.ru/LotRO_Legacy/FontRestorator
**Cel:** Zmiana czcionek w zasobach gry, aby przywrocic mozliwosc wyswietlania rosyjskich tekstow
**Kontekst:** LOTRO nie obsluguje natywnie Cyrylicy — czcionka musi byc podmieniona

---

## 4. Techniczne szczegoly formatu DAT

### 4.1 Struktura pliku DAT (Turbine format)

Z analizy kodu Python (jtauber/lotro) i DATUnpacker:

```
SUPERBLOCK (1024 bajtow):
  Offset 0x101: Magic 0x4C50 ("LP")
  Offset 0x140: Magic 0x5442 ("TB")
  Offset 0x144: block_size
  Offset 0x148: file_size (musi = rozmiar pliku na dysku)
  Offset 0x14C: version
  Offset 0x150: version_2
  Offset 0x154: free_head (wolne bloki — linked list)
  Offset 0x158: free_tail
  Offset 0x15C: free_size
  Offset 0x160: directory_offset

DIRECTORY (drzewo B-tree):
  8 bajtow: puste (assert zeros)
  62 * 8 bajtow: subdirectory pointers (block_size, dir_offset)
  4 bajty: count (liczba wpisow)
  count * 32 bajty: file entries:
    unk1 (4), file_id (4), file_offset (4), size1 (4),
    timestamp (4), version (4), size2 (4), unk2 (4)
```

### 4.2 Identyfikacja plikow tekstowych

```
file_id = 0x25XXXXXX

Sprawdzenie: (file_id >> 24) == 0x25

0x25 = TextFileMarker — high byte FileId identyfikuje podplik jako tekstowy
```

### 4.3 Struktura SubFile (tekstowy)

```
SubFile:
  FileId      (4 bajty, int)
  Unknown1    (4 bajty)
  Unknown2    (1 bajt)
  FragCount   (VarLen — 1 lub 2 bajty)
  Fragment[]  (FragCount elementow)

Fragment:
  FragmentId  (8 bajtow, ulong) — odpowiada GossipId
  PieceCount  (int)
  Piece[]     (PieceCount elementow):
    Length    (VarLen)
    Data      (UTF-16LE, Length bajtow)
  ArgRefCount (int)
  ArgRef[]    (ArgRefCount * 4 bajty)
  ArgStringGroupCount (byte)
  ArgStringGroup[]:
    StringCount (int)
    String[]    (VarLen length + UTF-16LE data)

VarLen encoding:
  0-127:     1 bajt (wartosc bezposrednio)
  128-32767: 2 bajty (high bit = 1 na pierwszym bajcie)
```

### 4.4 Funkcje datexport.dll (P/Invoke)

```
OpenDatFileEx2(handle, path, flags, out masterMap, out blockSize,
               out vnumDatFile, out vnumGameData, out datFileId,
               out datIdStamp[64], out firstIterGuid[64])

GetNumSubfiles(handle) -> count
GetSubfileSizes(handle, fileIds[], sizes[], iterations[], offset, count)
GetSubfileVersion(handle, fileId) -> version
GetSubfileData(handle, fileId, buffer, unknown, out version)
PutSubfileData(handle, fileId, buffer, unknown, size, version, iteration, unknown2)
PurgeSubfileData(handle, fileId)
Flush(handle)
CloseDatFile(handle)
```

**Flagi otwarcia:**
- `OpenFlagsReadWrite = 130` (Read=2 | Write=128)

---

## 5. Workflow tlumaczenia (rosyjski projekt)

```
SETUP (jednorazowy):
  1. Tlumacze rejestruja sie na translate.lotros.ru
  2. Administrator importuje eksport z DAT do bazy (file_id, gossip_id, english_text)
  3. Tlumacze loguja sie na web i tlumacza stringi
  4. Moderatorzy zatwierdzaja tlumaczenia (approved = 1)

CODZIENNE UZYCIE (gracze):
  5. Gracz pobiera i instaluje Legacy 3.0
  6. Legacy pobiera patche z serwera (~700 MB full, micro-updates dziennie)
  7. Legacy patchuje client_local_English.dat
  8. Legacy uruchamia gre z flagami -disablePatch -skiprawdownload

TLUMACZE (starszy Python patcher):
  9.  Login do translate.lotros.ru (email + haslo)
  10. Wybor zakresu: "ostatnia godzina/dzien/tydzien" lub custom daty
  11. GET /groupware/get/{timestamp} z filtrami (translator, approved)
  12. Odpowiedz: pipe-delimited identyczny z naszym formatem
  13. Tworzenie SQLite patcha lokalnie
  14. PATCH_IT() — wstrzykniecie do DAT
  15. Natychmiastowy podglad tlumaczenia w grze

UPDATE GRY:
  16. Oficjalny launcher nadpisuje DAT (jesli brak ochrony)
  17. Legacy/patcher musi re-aplikowac tlumaczenia
  18. Legacy 3.0 robi to automatycznie
```

---

## 6. Ochrona DAT — porownanie podejsc

### Rosyjskie podejscie: -disablePatch

```
TurbineLauncher.exe -nosplash -disablePatch -skiprawdownload

Plusy:
  + Proste — jedna flaga
  + Nie modyfikuje plikow systemu
  + Gra startuje normalnie

Minusy:
  - Undokumentowana flaga — SSG moze ja usunac w kazdym momencie
  - Nie blokuje WSZYSTKICH zmian, tylko oficjalny patcher
  - Gracz musi pamietac o uruchamianiu przez launcher
  - Nie blokuje Game Updates (update moze sie nalozyc nieswiadomie)
```

### Nasze podejscie: attrib +R

```
attrib +R "client_local_English.dat"
start /wait "" "TurbineLauncher.exe"
attrib -R "client_local_English.dat"

Plusy:
  + OS-level protection — launcher NIE MOZE obejsc
  + Blokuje WSZYSTKO (patche, update'y, kasowanie)
  + Wymusza swiadomy workflow update'u
  + Niezalezne od flag launchera

Minusy:
  - Wymaga admina (Program Files)
  - Blokuje tez game updates (celowe, ale wymaga explicit flow)
  - Trzeba pamietac o odblokowaniu po grze
```

### Werdykt

**Nasze podejscie jest znacznie silniejsze.** OS-level read-only jest niezalezny od implementacji launchera.
Rosyjski -disablePatch polega na wewnetrznej flagi programu ktora moze zniknac.

---

## 7. Detekcja update'ow — porownanie

### Rosyjskie podejscie: reaktywne

```
1. Legacy sledzni zmiany w client_local_English.dat
2. Jesli plik zostal zmodyfikowany przez inny program -> wykrycie
3. Propozycja re-pobrania oryginalnego DAT + re-patchowania
4. Brak proaktywnego sprawdzania forum / wersji gry
```

### Nasze podejscie: proaktywne (dwu-warstwowe)

```
WARSTWA 1 — Forum Checker (proaktywny alert):
  - Scraping forums.lotro.com/release-notes
  - Regex: Update\s+(\d+(?:\.\d+)*)\s+Release\s+Notes
  - Porownanie z data/last_known_game_version.txt
  - WIEMY o update zanim gracz cokolwiek zrobi

WARSTWA 2 — DAT vnum (twarde potwierdzenie):
  - OpenDatFileEx2() -> vnumDatFile, vnumGameData
  - Zmienia sie gdy oficjalny launcher zaktualizuje DAT
  - Potwierdza ze gracz FAKTYCZNIE zainstalowal update
  - OBECNIE IGNOROWANE (out _) — DO NAPRAWY (M1 #14)

FLOW:
  Forum: nowy  + DAT: stary  -> "Zaktualizuj gre!" + blokuj launch
  Forum: nowy  + DAT: nowy   -> "Zpatchuj ponownie" + pozwol
  Forum: stary + DAT: stary  -> "OK, grasz" + launch
```

### Werdykt

**Nasze podejscie jest lepsze koncepcyjnie** — proaktywna detekcja zamiast reaktywnej.
Ale rosyjskie podejscie DZIALA (bo jest prostsze), a nasze ma known bug
(GameUpdateChecker zapisuje wersje forum natychmiast, nie po faktycznym update).

---

## 8. Porownanie architektur

| Aspekt | Rosyjski projekt | Nasz projekt (LotroKoniecDev) |
|--------|-----------------|-------------------------------|
| **Jezyk** | Python 2.7 (stary), C++ z Qt (launcher), C# (Elanor) | C# 13, .NET 10.0 |
| **Architektura** | Monolit (kazde narzedzie osobne) | Clean Architecture (5 warstw) |
| **DAT library** | Wlasna C++ (LotroDat) + datexport.dll (stary) | datexport.dll via P/Invoke |
| **Error handling** | try/except, globalne zmienne | Result monad (railway-oriented) |
| **Testy** | Brak widocznych | ~550 assertions (xUnit + FluentAssertions) |
| **DI** | Brak (globalne zmienne gl.*) | Microsoft.Extensions.DI |
| **Web platform** | Custom (translate.lotros.ru) | Planowany Blazor SSR (M3) |
| **Desktop app** | Legacy 3.0 (C++ Qt) | Planowany WPF (M4) |
| **CLI** | Brak osobnego CLI | Istniejacy, dzialajacy CLI |
| **Baza danych** | Custom web + SQLite (patche lokalne) | Planowany MSSQL + EF Core (M2) |
| **Typy patchy** | Text, Font, Image, Sound, Texture, Loadscreen, Video | Tylko Text (na razie) |
| **Ochrona DAT** | -disablePatch flag | attrib +R (OS-level) |
| **Detekcja update** | Reaktywna (sledzenie zmian pliku) | Proaktywna (forum + DAT vnum) |
| **Multi-language** | Hardcoded Russian | Schema multi-language od dnia 1 |
| **Format danych** | file_id\|\|gossip_id\|\|content\|\|args | IDENTYCZNY |
| **Separator** | \<--DO_NOT_TOUCH!--\> | IDENTYCZNY |
| **TextFileMarker** | 0x25 (high byte FileId) | IDENTYCZNY |
| **API** | REST (GET) z timestamp filtering | Planowany REST (M3) |
| **Auth** | Email + password, role-based | Planowany OpenIddict (M5) |
| **Platforma** | Windows + Linux (Proton) | Windows only (na razie) |
| **Instalator** | Standalone installer | Planowany MSIX/Inno Setup (M4) |
| **Auto-update** | Tak (Legacy 3.0) | Planowany (M4 #69) |
| **Community** | 8+ lat, aktywna, 100% tlumaczenia | 1 osoba (na razie) |
| **Licencja** | GPL-3.0 (Elanor), MIT (patcher) | OSS |
| **Dokumentacja** | FAQ, gajdy na stronie | CLAUDE.md, PROJECT_PLAN.md |

---

## 9. Czego mozemy sie nauczyc

### 9.1 Co zrobili dobrze

1. **Modularnosc patchy** — nie tylko tekst, ale font/image/sound/texture/loadscreen/video.
   My mamy tylko tekst. W przyszlosci warto rozwazyc czcionki polskie (polskie znaki)
   i przetlumaczone obrazy/mapy.

2. **Mikro-aktualizacje** — codzienne pobieranie najnowszych tlumaczen bez czekania
   na pelny patch. Odpowiednik naszego planowanego API GET /api/v1/translations/export.

3. **Wlasna biblioteka C++ (LotroDat)** — uniezaleznili sie od zamknietej datexport.dll.
   My jestesmy zaleznie od datexport.dll. Jezeli Turbine/SSG zmieni format, nie mamy
   kontroli. Oni moga sami naprawic swoją biblioteke.

4. **System rol** (translator/moderator/developer/administrator) — pozwala na kontrole
   jakosci. Tlumacz widzi tylko swoje texty + zatwierdzone, moderator widzi wszystko.

5. **Timestamp-based filtering** — pobieranie tlumaczen z ostatniej godziny/dnia/tygodnia.
   Genialne dla iteracyjnego workflow.

6. **Auto-update programu** — Legacy 3.0 aktualizuje sie automatycznie.
   Gracz nigdy nie musi recznie pobierac nowej wersji.

7. **Linux/Steam Deck support** — rozszerzyli baze uzytkownikow.

8. **Font Restorator** — osobne narzedzie do podmiany czcionek. LOTRO nie obsluguje
   natywnie Cyrylicy. Dla polskich diakrytykow NIE jest potrzebny — Latin Extended
   dziala natywnie.

### 9.2 Co zrobili zle (z czego mozemy sie nauczyc)

1. **Python 2.7** — przestarzaly jezyk, brak typow, globalne zmienne (gl.*).
   Nasz C# 13 z nullable references jest znacznie lepszy.

2. **Brak testow** — zadnych widocznych testow w repozytoriach.
   My mamy ~550 assertions.

3. **Globalne zmienne** — `GlobalVars.py` z mutable state wspoldzielonym miedzy modulami.
   Nasz DI + scoped lifetimes jest znacznie czystszy.

4. **SQL injection** — w UpdateGUI.py: string concatenation do budowania SQL queries.
   My uzywamy parametryzowanych queries (planowany EF Core).

5. **Plaintext passwords** — haslo przesylane GET parametrem w URL.
   Nasz planowany OpenIddict/JWT bedzie bezpieczniejszy.

6. **Brak Error handling** — globalne try/except z wx.MessageDialog.
   Nasz Result monad jest znacznie bardziej systematyczny.

7. **Monolit** — kazde narzedzie (patcher, launcher, font tool) to osobny projekt
   z duplikacja kodu. Nasz shared MediatR handlers eliminuja duplikacje.

8. **-disablePatch** — slabsza ochrona DAT niz nasze attrib +R.

### 9.3 Kluczowe wnioski dla naszego projektu

#### Czcionki polskie — NIE jest blokerem

Rosjanie musieli stworzyc Font Restorator aby dodac Cyrylike (alfabet rosyjski
nie jest obslugiwany natywnie przez LOTRO). Poczatkowo wydawalo sie ze polskie
diakrytyki (ą, ę, ó, ź, ż, ć, ś, ł, ń) beda mialy ten sam problem.

**Weryfikacja:** Polskie znaki wyswietlaja sie w grze poprawnie bez modyfikacji czcionek.
LOTRO obsluguje Latin Extended natywnie (w tym polskie diakrytyki).
Problem FontRestorator dotyczy wylacznie Cyrylicy i innych nie-lacinskich alfabetow.

**Wniosek:** Nie potrzebujemy odpowiednika FontRestorator. To upraszcza nasz pipeline
— wystarczy patchowac tylko tekst, bez modyfikacji czcionek.

#### API design (M3)

Ich API jest zaskakujaco proste i mozemy go zaadaptowac:
- Timestamp-based filtering (GET /translations?from={}&to={}&translator={})
- Role-based access (translator/moderator)
- Pipe-delimited response (identyczny z naszym formatem plikow)

#### Desktop App (M4)

Ich Legacy 3.0 features ktore warto zaadaptowac:
- Auto-update (sprawdzanie nowej wersji + pobieranie)
- Lock UI przy patchowaniu (zapobiega uszkodzeniu DAT)
- Progress bar z procentami
- Mikro-aktualizacje (daily pulls najnowszych tlumaczen)

---

## 10. Repozytoria (lista)

| Repo | URL | Tech | Status |
|------|-----|------|--------|
| Elanor | https://github.com/Endevir/Elanor | C# | Legacy |
| Text Patcher | https://github.com/Endevir/LOTRO-Enchanced-text-patcher | Python 2.7 | Legacy (v1.09) |
| LotroDat | https://git.endevir.ru/LotRO_Legacy/LotroDat | C++ | Active |
| Legacy.launcher | https://git.endevir.ru/LotRO_Legacy/Legacy.launcher | C++ Qt | Active |
| Legacy_v2 | https://git.endevir.ru/LotRO_Legacy/Legacy_v2 | C++ Qt | Deprecated |
| FontRestorator | https://git.endevir.ru/LotRO_Legacy (subfolder) | C++ | Active |
| DATUnpacker | https://github.com/Middle-earth-Revenge/DATUnpacker | C# | Archive |
| dat.py | https://github.com/jtauber/lotro | Python | Reference |

---

## 11. Podsumowanie — ich DNA vs nasze DNA

```
WSPOLNE DNA (ten sam fundament):
  ✓ datexport.dll (lub wlasna reimplementacja)
  ✓ TextFileMarker = 0x25
  ✓ Format: file_id||gossip_id||content||args_order||args_id||approved
  ✓ Separator: <--DO_NOT_TOUCH!-->
  ✓ Pipeline: export DAT -> baza -> UI tlumaczen -> tlumacz -> export -> patch DAT
  ✓ VarLen encoding w binarnych subfiles
  ✓ Fragment z Pieces + ArgRefs + ArgStrings

NASZE PRZEWAGI (architektoniczne):
  ✓ Clean Architecture (5 warstw) vs ich monolit
  ✓ Result monad vs try/catch
  ✓ ~550 test assertions vs brak
  ✓ Shared MediatR handlers (CLI + Web + WPF) vs duplikacja
  ✓ attrib +R (OS-level) vs -disablePatch (undocumented flag)
  ✓ Proaktywna detekcja update (forum + DAT vnum) vs reaktywna
  ✓ Multi-language schema od dnia 1 vs hardcoded Russian
  ✓ DI + scoped lifetimes vs globalne zmienne
  ✓ C# 13 / .NET 10.0 vs Python 2.7 / C++ Qt

ICH PRZEWAGI (mature product):
  ✗ 8+ lat doswiadczenia z pipeline
  ✗ 100% tlumaczenia (setki tysiecy stringow)
  ✗ Aktywna community tlumaczow
  ✗ Modularnosc patchy (text + font + image + sound + texture + loadscreen + video)
  ✗ Wlasna C++ biblioteka DAT (niezaleznosc od datexport.dll)
  ✗ Auto-update programu (Legacy 3.0)
  ✗ Linux/Steam Deck support
  ✗ Font Restorator (czcionki Cyrillic)
  ✗ Dzialajaca web platforma z API
  ✗ Mikro-aktualizacje (dzienne pobieranie najnowszych tlumaczen)

WAZNY WNIOSEK:
  Rosjanie musieli stworzyc FontRestorator dla Cyrylicy.
  Polskie diakrytyki (Latin Extended) dzialaja natywnie w LOTRO.
  Nie potrzebujemy odpowiednika FontRestorator — upraszcza to nasz pipeline.
```

---

## Zrodla

- [translate.lotros.ru](http://translate.lotros.ru/) — Glowna strona projektu
- [translate.lotros.ru/pages/download.html](http://translate.lotros.ru/pages/download.html) — Pobieranie
- [translate.lotros.ru/pages/legacy-v2.html](http://translate.lotros.ru/pages/legacy-v2.html) — Наследие v2
- [translate.lotros.ru/pages/legacy-v3.html](http://translate.lotros.ru/pages/legacy-v3.html) — Наследие 3.0
- [translate.lotros.ru/131-vershina-pokorena.html](http://translate.lotros.ru/131-vershina-pokorena.html) — 100% milestone
- [GitHub: Endevir/Elanor](https://github.com/Endevir/Elanor) — C# launcher (GPL-3.0)
- [GitHub: Endevir/LOTRO-Enchanced-text-patcher](https://github.com/Endevir/LOTRO-Enchanced-text-patcher) — Python patcher (MIT)
- [git.endevir.ru/LotRO_Legacy](https://git.endevir.ru/LotRO_Legacy) — Organizacja na Gitea
- [git.endevir.ru/LotRO_Legacy/LotroDat](https://git.endevir.ru/LotRO_Legacy/LotroDat) — C++ DAT library
- [git.endevir.ru/LotRO_Legacy/Legacy.launcher](https://git.endevir.ru/LotRO_Legacy/Legacy.launcher) — Launcher source
- [GitHub: Middle-earth-Revenge/DATUnpacker](https://github.com/Middle-earth-Revenge/DATUnpacker) — DAT Unpacker (C#)
- [GitHub: jtauber/lotro](https://github.com/jtauber/lotro) — Python DAT explorer
- [Steam Guide: rusyfikator LOTRO](https://steamcommunity.com/sharedfiles/filedetails/?id=2432868354) — Instrukcja Steam
- [lotro-wiki.com/wiki/Translations](https://lotro-wiki.com/wiki/Translations) — LOTRO Wiki
- [lotro-mindon.ru/content/o-rusifikacii-lotro](https://lotro-mindon.ru/content/o-rusifikacii-lotro) — Community article
