# Update 48.8 → 49.1 ("In Good Company") — Test Results

**Data:** 2026-08-02
**Operator:** Claude (automated) + user (in-game verification + update trigger)
**Status:** ✅ **ALL TESTS PASSED — z PIERWSZĄ w historii projektu realną ofiarą update'u (1/8 fragmentów revertnięty przez wymianę SubFile'a)**
**Redaction note:** quest-text content in this file uses the synthetic equivalents established by
LEGAL-08 (2026-07); verbatim originals live only in the gitignored `intel/update-49/`.

## Verdict

**7/8 translacji przetrwało major update 48.8 → 49.1 bajtowo nietkniętych; 1/8 revertnięty do
angielskiego oryginału — bo SSG zmodyfikował jego SubFile.** To pierwsza empiryczna obserwacja
granicy chunk-based survival po 9 testach na żywo:

1. ✅ **In-game (user):** „Stylizacje przeżyły"; wątpliwość co do „Ekwipunek" rozstrzygnięta
   exportem — nasz fragment jest nietknięty (user patrzył na inny string „Equipment" w grze)
2. ⚠️ **Export survival: 7/8** — pair-level 8/8 obecnych, content-level 7/8 polskich;
   `620757435||225138404` („Wejdź do Śródziemia") revertnięty do `Enter Middle-earth`
3. ✅ **Diff stability:** dokładnie 1 para w hunkach diffa (−polski/+angielski revert);
   pozostałe 7 par — 0 matchów w 694 hunkach
4. ✅ **Mechanizm revertu zlokalizowany:** SubFile 620757435 urósł 1019 → 1023 fragmentów
   (+4 nowe) → launcher wymienił CAŁY chunk → wszystkie nasze fragmenty w nim wracają do
   defaultu, choć tekst naszego fragmentu sam w sobie się NIE zmienił (`Enter Middle-earth`
   przed i po). Kontrpróba: SubFile'e ocalałych par mają identyczne liczby fragmentów
   (620757027: 427→427 · 620861331: 5→5 · 620871150: 26→26 · 620759036: 15→15)

**Nowy model survival (uściślenie):** przeżycie jest **per-SubFile (chunk), nie per-fragment**.
Update, który modyfikuje SubFile (dodaje/zmienia dowolny fragment w nim), revertuje w nim
WSZYSTKIE nasze translacje. SubFile'e nietknięte przez update = byte-for-byte survival (jak
dotąd w 100% przypadków). Naprawa = zwykły re-patch (hash-mismatch PATCH path), a w modelu
TMS — dokładnie flow invalidation ze spec 0001.

## Intel summary

### DAT state comparison

| Metric | 48.8 (baseline) | 49.1 (post-update) | Delta |
|--------|-----------------|--------------------|-------|
| Size (B) | 1,893,807,856 | 1,894,856,432 | **+1,048,576 (+0.055%)** — znowu równy 1 MiB |
| SHA256 | `4E9A8106…5AFD88` | `1B94B27A…DE17A` | changed |
| LastWriteTime | 2026-07-11 02:47:46 | 2026-08-02 13:46:00 | — |
| TotalTextFiles | 278,983 | 281,253 | **+2,270 (+0.81%)** |
| TotalFragments | 793,672 | 800,864 | **+7,192 (+0.906%)** |
| Export output size | 82,557,600 B | 83,104,955 B | +547,355 B |

**Obserwacja:** treściowo to pełnoprawny major (+7,192 fragmentów — więcej niż 48.0!), ale DAT
urósł tylko o JEDEN blok 1 MiB — launcher nadpisywał istniejące chunki in-place (m.in. nasz
620757435) i dołożył jeden blok alokacji. Drugi przypadek z rzędu równego +1 MiB (48.7 tak samo).

### Diff summary (export-48.8.txt vs export-49.txt)

- Diff file: 1,760,882 B, **694 change hunks** (żyje tylko w gitignored `intel/update-49/` —
  verbatim game text, LEGAL-08; statystyki zachowane tutaj)
- **2,356 linii usuniętych** / **9,548 dodanych** (net +7,192 = dokładnie TotalFragments delta ✔)
- **Dokładnie 1 polish para w diffie** (revert): `-…Wejdź do Śródziemia` / `+…Enter Middle-earth`;
  pozostałe 7 par: 0 matchów

### Survival — 7/8 content-level (export-49.txt)

| # | FileId | GossipId | Content (synthetic where quest text) | 48.8 line → 49 line | Survived |
|---|--------|----------|--------------------------------------|---------------------|----------|
| 1 | 620871150 | 218649169 | `'Mamy znak, <--DO_NOT_TOUCH!-->! Szare ćmy…` | 255044 → 255112 | ✅ |
| 2 | 620759036 | 218649169 | `'PL - We cannot let the old warden rouse…` | 19821 → 19855 | ✅ |
| 3 | 620757435 | 225138404 | `Wejdź do Śródziemia` → `Enter Middle-earth` | 7063 → 7092 | ❌ REVERT |
| 4 | 620757027 | 9795381 | `Kliknij tutaj aby wybrać swój tytuł` | 3798 → 3827 | ✅ |
| 5 | 620861331 | 228870261 | `Ekwipunek` | 227830 → 227898 | ✅ |
| 6 | 620757027 | 29271026 | `Podstawowe statystyki` | 3879 → 3908 | ✅ |
| 7 | 620757027 | 103943794 | `Pokaż wszystkie` | 3681 → 3710 | ✅ |
| 8 | 620757027 | 85383154 | `Stylizacje` | 3765 → 3794 | ✅ |

„Ekwipunek" (par #5): **przeżył** — in-game wątpliwość usera wynikała z patrzenia na inne
wystąpienie „Equipment" w UI (inny FileId/GossipId), nie na nasz fragment 620861331||228870261.

### Vnum — **6. cykl z rzędu bez zmiany (drugi major)**

```
Before 49:  VnumDatFile=112, VnumGameData=3
After 49:   VnumDatFile=112, VnumGameData=3  ← UNCHANGED
```

45.x → 47.x → 48.0 → 48.7 → 48.8 → **49.1**: vnum 112/3 stałe przez 6 cykli, w tym DWA majory.

### Forum version — „49.1"

Preflight forum-fetcher zwrócił **ForumVersion = 49.1** podczas WRITE-path testu (SaveBaseline
zapisał `49.1|112|3|<hash>`). Czyli SSG wydał już patch 49.1 po premierze 49 (2026-07-22) i klient
po dzisiejszym update jest na **49.1** — tak należy zarejestrować GameVersion w TMS.

### datexport.dll compatibility (49.1 schema)

- ✅ **READ path (export) na 49.1 DAT:** 800,864 fragments, zero errors
- ✅ **WRITE path (patch) na 49.1 DAT:** 8/8 applied, 0 skipped, zero warnings
  - write-test DAT: `8EE2F022…73730`, size 1,894,856,432 (size-neutral re-patch), backup auto-created
  - UWAGA: patch użył repo `translations/polish.txt` = syntetyczny post-LEGAL-08 (`f3dd657c…`) —
    OK na kopii testowej; NIE aplikować na live DAT (patrz BASELINE §rozwidlenie)
- Schema niezmieniona (vnum 112/3); pełna kompatybilność wsteczna READ+WRITE.

### Deviation od poprzednich protokołów — launch pominięty (deliberate)

Tym razem **nie odpalaliśmy naszego `launch`** przed update'em: repo polish.txt jest po LEGAL-08
syntetyczny (hash mismatch → PATCH path wstrzyknąłby syntetyczny tekst do żywej gry). Update
poszedł przez oficjalny LotroLauncher bezpośrednio. Obie gałęzie simplified flow były już
zwalidowane żywymi update'ami (PATCH — 48.0, SKIP — 48.7); ten cykl testował czysty
resident-survival. Version file po WRITE-teście przywrócony do `48.8|112|3|40b613a2…` (stan
zgodny z tym, co realnie rezyduje w live DAT).

## Kluczowe odkrycia / memory-worthy

1. **Pierwsza realna ofiara update'u po 9 testach: survival jest per-SubFile.** SSG dodał 4
   fragmenty do SubFile 620757435 → launcher wymienił cały chunk → nasz fragment wrócił do
   angielskiego, mimo że jego własny tekst się nie zmienił. 7 par w nietkniętych SubFile'ach —
   byte-for-byte survival, jak zawsze.
2. **To jest dokładnie casus, dla którego istnieje spec 0001.** Import export-49 do TMS wykryje
   zmianę source text dla 225138404 (diff) → invalidation → re-approve → nowy artefakt → CLI
   sync → re-patch przywraca polski. Pierwszy real-world przebieg całej pętli invalidation.
3. **Wniosek „protection NOT needed" stoi.** `attrib +R` i tak by nie uratował fragmentu
   (launcher wymienia chunk w ramach legalnego update'u) — a naprawa to zwykły re-patch.
   Model „translations survive" zyskuje uściślenie, nie zaprzeczenie.
4. **Major może być size-cheap:** +7,192 fragmentów (więcej niż 48.0) przy wzroście DAT o
   dokładnie +1 MiB — in-place chunk rewrites + 1 blok alokacji. Drugi kolejny przypadek
   równego +1 MiB.
5. **Vnum 6 cykli bez ruchu (2 majory); forum-fetcher żywy i poprawny („49.1").**
6. **Manual-export gotcha (dla TMS):** export z patchowanego DAT niesie nasze polskie treści
   jako „source" dla rezydentnych par. Przy imporcie 49.1: 7 par bez zmiany (polski==polski,
   brak invalidation), 1 para z revertem → source-change → invalidation. Działa na naszą
   korzyść, ale warto pamiętać, że „source text" tych rzędów w TMS to historycznie nasz polski.

## Analiza scenariuszy diffu importu (spec 0001) wobec per-SubFile revertu — 2026-08-02

Policzono z par `(FileId, GossipId)` obu exportów — przewidywany `ImportSummary` dla stanu
DB ≈ 48.8 (prod może mieć starszy baseline → liczby większe, mechanika identyczna):
**Added 7,836 · Source-changed 1,644 · Removed 644 (0.08% — daleko od guardu 20%) ·
Unchanged 791,384** (net +7,192 ✔).

### Scenariusz A — SSG realnie zmienił tekst przetłumaczonego wiersza (działa jak zaprojektowano)

Zmiana tekstu ⇒ SubFile zmodyfikowany ⇒ chunk wymieniony ⇒ **w DAT gracza już jest świeży
angielski** (ten test dowodzi tego mechanizmu empirycznie — spec §"fallback-to-English physics"
pkt 1 przestał być założeniem). Import: source-changed → NeedsReview + `PreviousSourceText` →
wiersz wypada z artefaktu → ETag/hash → re-patch bez niego → gra pokazuje angielski (poprawnie)
→ re-translate → approve → artefakt odzyskuje wiersz → polski wraca. Pełna pętla zamknięta.

### Scenariusz B — collateral revert (NASZ przypadek; NOWA klasa, luka kliencka)

SSG **nie** zmienił naszego tekstu, ale zmodyfikował SubFile (tu: +4 sąsiednie fragmenty) ⇒
chunk wymieniony ⇒ polski znika z DAT gracza. Dla TMS (przy czystym angielskim source) wiersz
jest **Unchanged** → zostaje Approved → artefakt bajtowo identyczny → 304 + hash match → SKIP →
**angielski w grze na czas nieokreślony; system sam tego nie wykryje**. Samoleczenie następuje
dopiero „przy okazji" pierwszej dowolnej zmiany artefaktu (hash mismatch → pełny re-patch
przywraca też wiersze collateral). Kluczowe: **residency jest per-gracz** (każdy patchował w
innym momencie), więc TMS z zasady nie może wiedzieć, co komu wypadło — naprawa musi być
**kliencka**: launch sentinel „DAT zmienił się od naszego ostatniego patcha → wymuś re-patch"
(TP-00 #377 — teraz z twardym dowodem; kandydat do promocji).

**Promień rażenia policzony (2026-08-02, z par obu exportów):** update 49 dotknął **1,277 z
277,420** istniejących SubFile'ów tekstowych (0.46%). Siedzi w nich 14,038 fragmentów (1.75%
korpusu 800,864): 1,644 source-changed (angielski w grze CELOWY do re-approve), 214 nowych
(untranslated), **≈12,180 collateral (1.52% korpusu)** — przy w pełni przetłumaczonym
rezydentnym korpusie tyle WAŻNEGO polskiego revertnąłby ten jeden major. 98.5% przeżywa
bajtowo. Minor patche dotykają ułamka tego (48.7→48.8: ~5 SubFile'ów).

**Subtelność projektowa sentinela (ujawniona tym testem):** wymuszony re-patch zaraz po update,
ze STARYM artefaktem, nadpisałby świeży angielski stale-polskim dla wierszy z realną zmianą — a
usunięcie wiersza z artefaktu później **nie przywraca** angielskiego w DAT (patch nie pisze
braków) → maskowanie do następnej wymiany chunka. Sentinel musi więc: najpierw świeży artefakt
(ETag), najlepiej dopiero gdy nowa wersja jest w TMS przetworzona; offline → nie patchować.

### Corollary czasowy scenariusza B — patch-przed-update (2026-08-02, pytanie usera)

Nasz flow patchuje **przed** oficjalnym launcherem (sync → hash-check → patch → fire-and-forget;
update aplikuje się PO nas — log 48.7: my 23:05:58, update ~23:06). Skutki:

- **Nawet w pełni przygotowany TMS nie chroni pierwszej sesji po update.** Gracz z kompletnie
  re-approvowanym artefaktem: launch #1 → PATCH wgrywa wszystko na STARY DAT → update wymienia
  chunki i wymazuje właśnie-wgrane wiersze (zmienione + collateral) → angielski w grze.
- **Restart nie pomaga:** launch #2 → 304 + hash match → SKIP → angielski zostaje. Trigger jest
  wyłącznie hashem pliku tłumaczeń, a ten się nie zmienił. Naprawa wyłącznie rykoszetem od
  dowolnej następnej zmiany artefaktu (czyjeś approve).
- **Collateral row (Enter Middle-earth) nie naprawi się NIGDY pracą adminów** — dla TMS jest
  „Unchanged", nie ma czego re-approvować; tylko rykoszet lub sentinel.
- **Z sentinelem (DAT-fingerprint w version file → wymuszony re-patch świeżym artefaktem)
  koszt spada do dokładnie jednego restartu.** Zero restartów wymagałoby patchowania po
  update = monitorowanie launchera = udowodnione szkodliwe legacy flow. Opcjonalny szlif:
  stored ForumVersion ≠ forum ⇒ pomiń patch na launchu, na którym i tak przyjdzie update.

### Przestrzeń napraw klienckich — pogłębiona 2026-08-02 (dyskusja z właścicielem)

**Anatomia locków (hipoteza do zbadania):** launcher SSG: patch-faza (trzyma DAT, pisze) →
ekran logowania (prawdopodobnie NIE trzyma DAT — do potwierdzenia!) → Play → spawnuje
lotroclient.exe (trzyma DAT do końca sesji; nasz patch ma już branch `GameAlreadyRunning`) →
launcher umiera. Sygnały „launcher exit / game start" (intuicja legacy flow) przychodzą **za
późno** — DAT już zajęty przez klienta; jedyna czysta interwencja to wtedy kill+relaunch
(= udokumentowane szkody legacy: double UAC/login, zabita sesja).

**Wariant „login-window" (nowy, nieprzetestowany):** elevated proces zostaje żywy po
odpaleniu launchera i polluje „DAT otwieralny do zapisu + mtime zmieniony od naszego baseline"
→ patchuje W TRAKCIE gdy gracz wpisuje hasło → gra startuje już po polsku. Bez killa, bez
podwójnego logowania, jedna elevacja. Wygrana ⇒ zero angielskich sesji przy zachowaniu
oficjalnego launchera jako updatera. Przegrany wyścig ⇒ fallback: sentinel-next-launch
(default) albo **opt-in kill-and-relaunch** z one-shot guardem (marker próby per
DAT-fingerprint ⇒ brak restart-loopa). **Niewiadome empiryczne:** (1) czy launcher trzyma
handle DAT na ekranie logowania (test: launcher na ekranie logowania + elevated próba otwarcia
RW); (2) czas pełnego re-patcha przy dużym korpusie (8 wierszy ≈ 0.7–5 s; 100k+ nieznany —
jeśli minuty, okno logowania nie wystarczy). Mitygacja czasu: **repair-set** — TMS zna z
importu listę SubFile'ów dotkniętych wersją; klient naprawia tylko wiersze artefaktu w
dotkniętych SubFile'ach (~14k fragmentów zamiast całego korpusu przy majorze).

**Synteza „update-day orchestrator" (2026-08-02, iteracja z właścicielem — jego kill-launcher
pomysł + login-window):** elevated watcher zostaje żywy po odpaleniu launchera i śledzi STAN
PLIKÓW (nie procesów — jak rosyjski Legacy): mtime DAT + próba otwarcia RW + quiesce (brak
zapisów przez N s). Gałęzie: (A) probe RW się udaje → **cichy patch in-place w oknie logowania,
zero killa, zero restartu** — user nawet nie wie; (B) update skończony (quiesce), ale handle
trzymany i klient NIE wystartował → **auto-kill LAUNCHERA pre-creds → patch → relaunch** —
user loguje się raz, wygląda jak zwykły flow update'u (kill launchera pre-sesja ≠ kill klienta
w trakcie sesji — szkoda legacy nie występuje); (C) klient już żyje → nic nie robimy, sentinel
naprawi następny launch. One-shot guard per DAT-fingerprint wyklucza restart-loop. Ryzyko
gałęzi B: fałszywy quiesce w trakcie wolnego downloadu → kill mid-update (launcher SSG jest
wznawialny/weryfikujący, ale okno quiesce musi być konserwatywne, np. 30+ s + launcher idle).
Detekcja przez screenshoty+LLM: ODRZUCONA — file-state probe + ew. tytuł okna (Win32) dają tę
samą informację deterministycznie, offline i za darmo; FileSystemWatcher/polling to koszt ~zero.

**Eksperymenty do wykonania (wszystkie lokalne, bez czekania na SSG poza E2):**
E1 — launcher na ekranie logowania: elevated próba otwarcia DAT RW (czy handle trzymany) —
2 min, rozstrzyga gałąź A vs B. E2 — realny update lub tryb repair/verify launchera jako
symulator cyklu write→release. E3 — benchmark pełnego patcha przy dużym korpusie (syntetyczny
polish.txt ~100k+ wierszy na KOPII DAT) — budżet czasowy okna + tak czy siak potrzebny dla M4;
mitygacja: repair-set. E4 — kill launchera na ekranie logowania → relaunch: czy wraca czysto
do logowania (bezpieczeństwo gałęzi B).

**Rosjanie — fakty potwierdzone w `docs/RUSSIAN_PROJECT_RESEARCH.md` (2026-02-09):** ich
detekcja jest **reaktywna, plikowa** — „Legacy śledzi zmiany w client_local_English.dat;
zmodyfikowany przez inny program → wykrycie → propozycja re-pobrania ORYGINALNEGO DAT +
re-patchowania"; po update gry „Legacy/patcher musi re-aplikować tłumaczenia — Legacy 3.0 robi
to automatycznie"; gra odpalana z `-disablePatch -nosplash -skiprawdownload`. Czyli: **ich
mechanizm = nasz sentinel** (file-state tracking, nie monitoring procesów), a „oryginalny DAT"
= trzymają pristine source (istotne też dla naszego echo-guarda). **NIEpotwierdzone:** czy
gracz Legacy widzi 0 czy 1 sesję z rosyjskim brakiem po update (czy Legacy orkiestruje
oficjalny update, czy naprawia dopiero na następnym swoim launchu). Brak danych o wymuszonym
restarcie — do ewentualnego doszczegółowienia z ich forów.

### Scenariusz C/D/E — Added / Removed / Re-added

Added (7,836) → wiersze untranslated, artefaktu nie dotykają. Removed (644) → soft
`RemovedInVersion`; wypadnięcie z artefaktu tylko gdy wiersz był Approved. Re-added → reguła
restore-status ze spec 0001. Bez niespodzianek.

### Scenariusz F — echo z patchowanego DAT (systemowa pułapka ceremonii manualnej)

Admin eksportuje z **własnego, spatchowanego** DAT ⇒ dla wierszy rezydentnych „source" w
exporcie to **nasz polski**, nie angielski. Spec 0001 zakłada angielski source — dwa przebiegi:

- **Czysta baza (source = prawdziwy angielski):** rezydentny wiersz importuje się jako
  „source-changed" (angielski→polski!) → **fałszywa inwalidacja każdego
  przetłumaczonego-i-rezydentnego wiersza** + nadpisanie source polskim. Przy 8 wierszach
  niewidoczne; przy dużym korpusie — masowa fałszywa inwalidacja co update (guard 20% nie
  łapie — to nie removal).
- **Zatruta baza (source = polskie echo z wcześniejszego importu — stan dzisiejszego prod dla
  8 wierszy):** rezydentne = „Unchanged" (polski==polski), a collateral revert wykrywa się jako
  source-changed (polski→angielski) → inwalidacja → pętla przypadkiem działa. Paradoks: zatrute
  source maskuje lukę B — kosztem tego, że TMS nie zna prawdziwego angielskiego tych wierszy
  (translator bez oryginału, przyszłe diffy porównują z polskim).

**Remedy (do decyzji właściciela, spec-0001 amendment + ticket):** echo-guard w imporcie
(incoming text == aktualny polski content wiersza → traktuj jako echo/unchanged, nie ruszaj
source) + jednorazowa naprawa zatrutych source'ów + docelowo eksport z czystego źródła
(revert-file generowany z TMS przed exportem albo czysta kopia DAT).
**Status:** echo-guard **wdrożony 2026-08-17 (#563 UR-20, spec 0012)** — porównanie po hashu
trójki `(TranslatedText, ArgsOrder, ArgsId)`, licznik `Echoed` w `ImportSummary`; naprawa
zatrutych source'ów = #564 (UR-21).

## Experiments E1–E4 — results (2026-08-02, #557)

### E3 — full-corpus patch benchmark: ✅ DONE — repair-set NIE jest wymagany czasowo

Setup: syntetyczny pełny korpus z export-49 (**800,865 wierszy danych**, treść = `PL ` + oryginał,
`approved=1`, format bez zmian, CRLF zachowane) → Release CLI → patch na **KOPII** DAT 49
z pre-utworzonym `.backup` (krok backupu = no-op „already exists" — zgodne z realnym update-day,
gdzie backup już istnieje) → uruchomienie z osobnego cwd, żeby `SaveBaseline` nie tknął żywego
`data/last_known_game_version.txt`.

| Run | Wiersze | Wall clock (cała komenda) | Applied / Skipped / Warnings |
|---|---|---|---|
| Full corpus | 800,864 | **14.7 s** | 800,864 / 0 / 0 |
| Repair-set-sized | 21,660 | **5.6 s** | 21,660 / 0 / 0 |

- Wall clock obejmuje WSZYSTKO: startup CLI, preflight (w tym forum fetch przez sieć),
  parsowanie pliku tłumaczeń (83 MB przy pełnym korpusie), patch wszystkich SubFile'ów, flush.
  Stały narzut (startup+preflight+parse małego pliku) ≈ 3–4 s ⇒ czysty patch pełnego korpusu
  ≈ 10–11 s.
- Weryfikacja realności zapisu: bench DAT urósł +5,242,880 B — spójne z prefiksem `PL `
  (~800k fragmentów × ~6.5 B UTF-16).
- Repair-set proxy: wszystkie wiersze korpusu w 3,921 FileIds obecnych w hunkach diffa
  48.8→49 (nadzbiór 1,277 dotkniętych istniejących SubFile'ów — zawiera też nowe SubFile'e).
- Sprzęt: maszyna maintainera (NVMe). Nawet ×5 na wolnym dysku mieści się w oknie logowania.

**Gating (spec 0012):** pełny re-patch korpusu mieści się w oknie logowania z dużym zapasem ⇒
**repair-set = opcjonalna optymalizacja, nie wymaganie MVP**. Draft AC „repair touches only
touched SubFiles" wypada z MVP.

### Nowe fakty odkryte przy przygotowaniu E1 (anatomia locków — uściślenie)

1. **`LotroLauncher.exe` ma manifest `requestedExecutionLevel=asInvoker`** — launcher sam się
   NIE elevuje. 2. **ACL katalogu gry: `BUILTIN\Users = RX` (read+execute), zero write** —
   zwykło-odpalony (nieelevowany) launcher **w ogóle nie może pisać do DAT**. Wnioski:
   - Do zapisu przy update launcher musi coś elevować (UAC consent przy starcie update'u?) albo
     user odpala go „jako administrator" — **E2 ma zidentyfikować, który proces realnie pisze**.
   - Sonda RW **musi być elevated** (nieelevowana dostaje ACL-owy ACCESS-DENIED, który maskuje
     stan sharing — potwierdzone self-testem skryptu).
   - Interpretacja E1: launcher nieelevowany może na ekranie logowania trzymać handle READ
     z restrykcyjnym sharingiem (np. `FileShare.Read`) — to też blokuje nasz otwór RW/ShareNone.
     Sonda odpowiada więc na pytanie operacyjne („czy MY możemy patchować"), nie na pytanie
     „czy launcher ma handle write".

### E1 — ✅ **OPEN-OK na ekranie logowania — launcher NIE trzyma DAT; gałąź A wykonalna**

Przebieg 2026-08-02 19:14–19:17 (`scripts/experiments/e1-rw-probe.ps1`, elevated; pełny log
w gitignored `intel/update-49/e1-probe-results.log`):

| Label | Procesy | Wynik | mtime DAT w chwili sondy |
|---|---|---|---|
| baseline | none | OPEN-OK | 13:46:00 (bez zmian — sonda jest nieinwazyjna, nie bumpuje mtime) |
| **login-screen** | LotroLauncher | **OPEN-OK** | 19:14:55 (launcher pisał do DAT ~16 s wcześniej, w fazie startowego checku — i już puścił) |
| in-game | (klient 64-bit) | **LOCKED 0x80070020** sharing violation | 19:15:53 (kolejny zapis przy starcie klienta/logowaniu) |

**Gating (spec 0012): gałąź A potwierdzona jako dominująca** — na ekranie logowania DAT jest
wolny, cichy in-place patch w oknie wpisywania hasła jest fizycznie możliwy (a z E3 wiemy, że
nawet pełny korpus = 14.7 s). Kontrola negatywna zachowuje się poprawnie (klient trzyma DAT
przez całą sesję). Gotcha narzędziowa: log pokazał `procs=none` przy in-game locku, bo filtr
skryptu nie znał **`lotroclient64`** (nowoczesny klient jest 64-bitowy, `x64\lotroclient64.exe`)
— skrypty poprawione; patcherowy `GameProcessDetector` zna `lotroclient64` od dawna (bez buga).

### E4 — ✅ **kill pre-creds czysty — 3× reprodukcja; relaunch nieodróżnialny od zwykłego startu**

Launcher na ekranie logowania → `taskkill /IM LotroLauncher.exe /F` → ponowny start (user,
3 powtórzenia): **każdy start launchera wygląda identycznie** — UAC prompt → check DAT → ekran
logowania; po killu ZERO dodatkowej weryfikacji/naprawy ponad standardowy startowy check; po
zalogowaniu gra wstaje normalnie. **Gałąź B bezpieczna** jako fallback. Bonus rozwiązujący
zagadkę `asInvoker`+ACL: **launcher elevuje się przez UAC przy każdym starcie** — dlatego może
pisać do DAT mimo Users=RX (a nasz orchestrator, sam elevated, może go killnąć).

### Finding E1-F1 — **mtime DAT jest wolatylny: launcher pisze do DAT przy KAŻDYM starcie**

Sekwencja mtime: 13:46:00 (spoczynek; baseline-probe NIE bumpuje) → **19:14:55 przy samym
starcie launchera** (żadnego update'u; size bez zmian) → **19:15:53 przy starcie klienta**.
Konsekwencja projektowa: **fingerprint size+mtime z draftu Tier 0 generowałby false-positive
co launch** (mtime rusza się w każdej sesji bez żadnej utraty tłumaczeń) → sentinel
zdegenerowałby się do force-re-patch przy każdym starcie. Korekta w spec 0012: detekcja przez
**content-sentinel** — odczyt próbki znanych przetłumaczonych fragmentów przez datexport READ
(milisekundy, zero fałszywych sygnałów w obie strony); alternatywa always-repatch (~15 s/start)
odrzucona jako bezcelowy 800k-wierszowy zapis do DAT co sesję. Finalna decyzja: #558 (Q1).

### E2 — ✅ **wykonany od ręki metodą wymuszonego downgrade'u (pomysł ownera) — pełny cykl update zarejestrowany**

**Metoda (nowa, powtarzalna):** elevated podmiana live DAT na backup 48.8 → launcher sam wykrył
stary stan pliku i odtworzył **realny cykl update 48.8→49.1** (delta widoczna na pasku
launchera) → `scripts/experiments/e2-dat-handle-monitor.ps1` (sonda co 1 s) przez cały cykl +
sesję gry. Pełny log: gitignored `intel/update-49/e2-handle-timeline.log` (2 przebiegi —
przerwa 19:40:22–19:41:53 to restart monitora przez usera przy ekranie logowania).

| t (2026-08-02) | Zdarzenie |
|---|---|
| 19:39:32 | Monitor start: DAT=48.8 (1,893,807,856 B), probe OPEN-OK, procs=none |
| 19:39:39 | LotroLauncher startuje (UAC) — probe **WCIĄŻ OPEN-OK przez ~11 s**: faza check+download NIE trzyma DAT |
| 19:39:51.007 | **LOCKED** — burst apply |
| 19:39:52.032 | **OPEN-OK**, size = 1,894,856,432 B (co do bajta rozmiar 49.1), mtime bump — **cały apply w JEDNYM ~1 s burście** |
| 19:40–19:42 | Ekran logowania: OPEN-OK stabilnie (launcher żywy) |
| 19:42:12 | **LOCKED, procs=lotroclient64** — klient przejmuje DAT na całą sesję; launcher znika przy spawnie klienta |
| →koniec | LOCKED przez sesję in-game; po wyjściu z gry user zamknął monitor |

**Wnioski (gating spec 0012):**

1. **Download ≠ apply — faza pobierania NIE trzyma DAT.** Probe-success mid-update JEST
   możliwy (~11 s wolnego DAT przed apply) ⇒ **convergent re-patch loop orchestratora jest
   konieczny i wystarczający**: nasz wczesny patch może zostać nadpisany burstem apply, ostatni
   zapis wygrywa, watch trwa do startu gry.
2. **Apply = pojedynczy ~1 s lock-burst** (delta ~5 MB / 1,277 SubFile'ów) ⇒ quiesce 30 s dla
   gałęzi B jest bardzo konserwatywny. Zastrzeżenie: duży major (nowy content GB-ami) może mieć
   dłuższe/wielokrotne bursty — monitor zostaje w arsenale na następny realny major SSG.
3. **Post-update login screen: OPEN-OK — gałąź A potwierdzona także w dniu update** (E1
   potwierdzał ją tylko przy zwykłym starcie).
4. Klient (`lotroclient64`) trzyma DAT od startu do końca sesji; launcher umiera przy spawnie
   klienta — sygnały procesowe raz jeszcze potwierdzone jako strukturalnie spóźnione.
5. **Tłumaczenia przeżyły wymuszony re-update** — user zweryfikował w UI gry („gwarantuję, że
   je widziałem"); stan DAT zbiegł do 49.1 co do bajta rozmiaru. Zgodne z modelem per-SubFile.
6. **Bonus metodologiczny:** forced-downgrade (podmiana DAT na starszy backup) = **powtarzalny
   symulator pełnego cyklu update** — testy orchestratora end-to-end bez czekania na SSG; przy
   okazji zwalidowana ścieżka „restore pristine DAT" (launcher czysto dociąga deltę).
7. mtime NIE drgnął przy starcie klienta w tym przebiegu (w E1 drgnął przy logowaniu) —
   wolatylność mtime jest nieprzewidywalna; finding E1-F1 (content-sentinel zamiast
   size+mtime) stoi w mocy.

## Experiment E5 — per-SubFile size/iteration snapshot (2026-08-17, #656) — ✅ **SYGNAŁ DZIAŁA: pokrycie 1,277/1,277, zero przegapionych**

**Metoda:** `scripts/experiments/e5-subfile-metadata-snapshot.ps1` (PR #657 + fix loadera):
read-only open (#629, **bez elevacji**), jedno `GetSubfileSizes` → CSV
`FileId,Size,Iteration[,Version]` dla wszystkich SubFile'ów; `-Diff` porównuje dwa CSV.
Gotcha narzędziowa: w hoście PowerShell zależności datexport.dll (msvcr71, msvcp71/90, zlib1T —
leżą obok niej w repo) wymagają `LoadLibraryExW` + `LOAD_WITH_ALTERED_SEARCH_PATH`; goły
`LoadLibraryW` po ścieżce absolutnej umiera z win32 error 126, bo loader szuka zależności
w katalogach hosta, nie DLL-ki.

**Odchylenie od protokołu (szczęśliwe):** między baseline'em after-patch a startem launchera SSG
wydał **realny update 49.1→49.3** — krok „plain launch" stał się pomiarem na żywym update, a
kontrola negatywna została wykonana po nim (gra już aktualna). Podmiana DAT na backup 48.8
okazała się **zbędna**: backup przediffowano **offline** przez `-DatPath` — nowa technika,
pomiar pełnego cyklu update bez dotykania żywej instalacji i bez re-downloadu.

Stany: 48.8 backup = 308,511 SubFile'ów (278,983 text) · 49.1 = 310,782 (281,253) ·
49.3 = 310,895 (281,366).

| Diff | size | iteration | version | added | removed |
|---|---|---|---|---|---|
| **Kontrola negatywna** (plain launch na 49.3, bez update) | **0** | **0** | **0** | **0** | **0** |
| **Realny update 49.1→49.3** (after-patch ↔ po launcherze) | 56 (54 text) | **57 (55 text)** | 0 | 122 (122 text) | 9 (9 text) |
| **48.8 backup ↔ live 49.3** (offline) | 725 (713 text) | **937 (921 text)** | 68 | 2,769 (2,768 text) | 385 (385 text) |

W obu pomiarach `any changed` = `iteration changed` — **iteration sama pokrywa komplet ruchu**
(size zgubił 1 SubFile przy realnym update i 212 przy 48.8→49.3; version to podzbiór iteration
i przy realnym update nie drgnął wcale — jako sygnał jest martwy, pętlę `-IncludeVersion`
można w detektorze pominąć).

**Cross-check z ground truth** (diff eksportów 48.8→49.1; ekstrakcja FileId z hunków
odtworzyła dokładnie **1,277** dotkniętych istniejących SubFile'ów z tego dokumentu):
**899 złapanych jako ruch iteration + 378 jako removed = 1,277/1,277, 0 przegapionych.**
Nowe-w-49: 2,642/2,644 obecne w `added` (brakujące 2 usunięte z powrotem przez 49.3 — spójne).
Bonus: 49.3 usunął 378 z SubFile'ów dotkniętych przez 49.1 i dodał 2,769 nowych — SSG
restrukturyzuje pliki między point-release'ami częściej, niż sugerował sam diff treści.

**Wnioski (gating #565 / spec 0012 Tier 0):**

1. **Predykat detektora: `iteration się ruszyła ∨ FileId zniknął` = chunk wymieniony od
   naszego patcha.** Pokrycie 100% względem znanego ground truth, zero fałszywych pozytywów
   (nasz `PatchingService` zachowuje iteration/version przy zapisie — potwierdzone: baseline
   zdjęty tuż po patchu, dalsze snapshoty bez patcha, żadnego szumu własnego).
2. **Kontrola negatywna czysta mimo E1-F1** — launcher pisze do DAT (mtime) przy każdym
   starcie, ale per-SubFile metadata stoi w miejscu. Dokładnie tej separacji szukaliśmy.
3. **Koszt:** open+`GetSubfileSizes` 0.21–0.23 s (warm) / 1.3–1.4 s (cold), bez elevacji —
   tańszy niż dzisiejsza ścieżka SKIP z hashem pliku tłumaczeń.
4. **Diff-set = repair-set za darmo** — detektor od razu wie, KTÓRE SubFile'e wymieniono
   (repair-set i tak opcjonalny po E3, ale przychodzi gratis).
5. **Tier-0 (#565) przechodzi z content-samplingu na snapshot metadanych:** przy `patch`
   zapisujemy mapę FileId→Iteration; przy `launch` jeden call + diff. Pytanie o szerokość
   próbkowania (K) znika w całości. Row-level source guard (ADR-0047/#659) zostaje bez zmian —
   to warstwa admisji zapisu, komplementarna wobec detekcji (zgodnie z komentarzem na #565
   z 2026-08-17).

## Pliki intel (gitignored `intel/update-49/`)

DAT backupy 48.8 + 49 + write-test (po ~1.76 GB), pełne exporty 48.8/49 (82.6/83.1 MB), pełny
diff (1.76 MB), snapshoty polish.txt (resident pre-LEGAL-08 + repo post-LEGAL-08) i version file,
snapshoty E5 (`e5-*.csv` + `.meta.txt`, ~7 MB każdy: after-patch, after-real-update-49.3,
after-plain-launch, backup-48.8-offline + listy zmienionych tekstowych FileId).
Committed tutaj: BASELINE.md + RESULTS.md (synthetic). Kopia robocza `data/exported.txt` =
export 49.1 (SHA256 `56F6D046…32B00`) — deliverable do importu w TMS (GameVersion **49.1**).
