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

## Powtórka E5 na drugim realnym update — 49.3 → 49.4 (2026-08-22) — ✅ **sygnał potwierdzony, size złapany na przegapieniu**

**Po co:** wnioski E5 (a przez to przeprojektowany #565) stały na **jednym** realnym update
(49.1→49.3). 49.4 wszedł na żywo 5 dni później i dał drugi punkt pomiarowy za ~10 minut,
bez pisania kodu. Protokół ten sam, tym razem wykonany zgodnie z planem (nie przez odchylenie).

**Przebieg:** snapshot `pre-49.4` na zapatchowanym DAT → czysty start `LotroLauncher.exe`
(bez `-disablePatch`) → launcher pobrał i nałożył 49.4 w ~55 s od startu → launcher zamknięty
przed logowaniem (kill pre-creds wg E4) → snapshot `after-real-update-49.4`.

Stany: 49.3 = 310,895 SubFile'ów (281,366 text) · **49.4 = 310,939 (281,410)**.
Rozmiar pliku DAT **bez zmiany** (1,894,856,432 B w obie strony) — trzecie potwierdzenie, że
SSG mieści update w slack space i whole-file fingerprint jest martwy (E1-F1).

| Diff | size | iteration | added | removed |
|---|---|---|---|---|
| **Kontrola negatywna** (17.08 `after-plain-launch` ↔ 22.08 `pre-49.4`) | **0** | **0** | **0** | **0** |
| **Realny update 49.3→49.4** | 7 (7 text) | **8 (8 text)** | 44 (44 text) | 0 |

**Kontrola negatywna jest tu mocniejsza niż w E5:** obejmuje **5 dni i wiele startów launchera**
(mtime DAT przesunął się w tym czasie na 17.08 16:53, a potem znowu przy starcie 22.08 16:05:07
z deltą rozmiaru 0), a per-SubFile metadata nie drgnęła ani razu. E5 mierzył jeden start
w oknie 10 minut — teraz wiadomo, że sygnał nie dryfuje od zwykłej eksploatacji.

**Twardy dowód `iteration ⊋ size`** — pełna tabelka 8 dotkniętych SubFile'ów:

| FileId | size przed → po | iteration przed → po |
|---|---|---|
| 621072883 | 3424 → 3456 | 26739 → 26751 |
| 621107670 | 56 → 60 | 26739 → 26751 |
| 621107676 | 56 → 58 | 26739 → 26751 |
| **621107677** | **92 → 92 (bez ruchu)** | **26739 → 26751** |
| 621127898 | 295 → 147 | 26749 → 26751 |
| 621127899 | 301 → 227 | 26749 → 26751 |
| 621127900 | 303 → 185 | 26749 → 26751 |
| 621128111 | 312 → 254 | 26749 → 26751 |

**621107677 to zmierzony miss detektora po rozmiarze** — chunk wymieniony, treść zmieniona,
rozmiar identyczny co do bajta. W E5 przewaga iteration nad size była policzona zbiorczo
(1 zgubiony SubFile z 57); tutaj widać konkretny wiersz i mechanizm. Predykat z #565
(`iteration się ruszyła ∨ FileId zniknął`) łapie go bez pudła.

**Nowy fakt o naturze `iteration`: to globalny, monotoniczny stempel zapisu, nie licznik
per-SubFile.** Przed update'em dotknięte SubFile'e miały dwie różne wartości (26739 z jednej
generacji, 26749 z drugiej); po update'cie **wszystkie osiem ma dokładnie 26751**. Wniosek
praktyczny dla #565: detektor może trzymać w mapie samą wartość i porównywać na równość — nie
ma potrzeby zakładać monotoniczności per SubFile ani obsługiwać przepełnienia licznika lokalnie.

**Przeżywalność naszych tłumaczeń: 8/8.** Dotknięte SubFile'e (621072883, 621107670/76/77,
621127898/99/900, 621128111) są rozłączne z rezydentnym setem (620757027, 620757435, 620759036,
620861331, 620871150) — 49.4 ruszył wyłącznie zakres 6210–6211, nasze wiersze siedzą w 6207–6208.
Zero collateral revertów, więc ten update nie dostarcza nowego przypadku scenariusza B.

**Koszt detektora:** open + `GetSubfileSizes` = **1.37 s cold / 0.22 s warm**, bez elevacji —
zgodne z E5 (0.21–0.23 s warm), na DAT większym o 44 SubFile'e.

### Gotcha operacyjna — `export` w powłoce nieinteraktywnej wymaga `-d`

`DatPathResolver` znajduje dziś dwie instalacje (Program Files + stary `data/client_local_English.dat`)
i pyta `Choose installation (1-2)`. W sesji bez stdin `Console.ReadLine()` zwraca null →
`Invalid choice` → `ExitCodes.FileNotFound`. Skryptowany export musi podawać ścieżkę jawnie:
`export -d "C:\Program Files (x86)\StandingStoneGames\The Lord of the Rings Online\client_local_English.dat"`.

## Experiment E6 — FileSystemWatcher w trakcie locka launchera (2026-10-10, #660) — ✅ **FSW WIDZI zapisy launchera w trakcie locka; cisza na DAT ≠ koniec update'u**

**Po co:** jedyną bramką gałęzi B (#566) jest „30+ s bez zapisów, gdy launcher trzyma DAT".
E2 odpytywał size/mtime raz na sekundę i widział ruch dopiero na końcu burstu — nikt nie
sprawdził, czy `FileSystemWatcher` widzi zapisy *w trakcie* trzymania pliku. Jeśli nie, długi
apply wygląda jak cisza i B zabija launcher w połowie zapisu.

**Okazja:** druga maszyna ownera (`adminpc`) — launcher nieuruchamiany od **2026-04-18** (ostatni
wpis `PatchClient.log`: „No data patching necessary"; 277,084 plików tekstowych vs 277,082
w baseline 47.2 ⇒ stan z ery 47.2, przed majorem 48.0). Pierwszy start = **realny, skumulowany
update 47.2 → 49.7** (dwa majory, 5,897 iteracji, 1.04 GB, co najmniej 17 plików DAT) — największa delta
w historii projektu. Przed startem: backup DAT (SHA256 `C9D06582…09F8`) + snapshot E5, więc
każdy kolejny przebieg to powtarzalny forced-downgrade.

**Narzędzie:** `scripts/experiments/e6-fsw-observability.ps1` = monitor E2 (sonda RW co 1 s,
size, mtime, procesy) + trzy watchery na DAT, po jednym na rodzaj zmiany (`size`, `lastwrite`,
`other` = attributes/creation/security/name/access); każde zdarzenie z czasem zgłoszenia
i rozmiarem pliku w tej chwili, zdarzenie `Error` (overflow bufora) logowane i liczone.
Przełączniki `-NoProbe` / `-NoStat` wyłączają nasze odpytywanie (patrz self-test niżej).
Uruchomienie: jeden elevated driver (jeden UAC) wykonujący stałe komendy z plików-flag —
monitor, start/zamknięcie launchera (`CloseMainWindow`, kill dopiero po 15 s), przywrócenie
backupu DAT; launcher startowany z procesu elevated. Pełne logi: gitignored `intel/update-49.7/`.

### Self-test (zanim ruszył launcher) — FSW sam z siebie NIE widzi zapisów przez trzymany uchwyt

Plik testowy na tym samym NTFS, pisarz trzyma jeden uchwyt 9 s: 3 dopisania po 64 KiB, potem
3 zapisy 1 KiB w miejscu (rozmiar bez zmian), potem close.

| Wariant monitora | Zdarzenia FSW w trakcie trzymania | Przy close |
|---|---|---|
| tylko FSW (`-NoProbe -NoStat`) | **0** | wszystkie 3 watchery |
| FSW + stat (`Get-Item`) | tylko po zmianie rozmiaru, **w momentach naszego odczytu** | tak |
| FSW + sonda RW | j.w. | tak |
| FSW + oba | j.w.; zapisy w miejscu — **nigdy** | tak |

Wniosek: na NTFS zmiana rozmiaru/czasu przez otwarty uchwyt dochodzi do watcherów dopiero, gdy
ktoś odczyta metadane pliku albo uchwyt się zamknie. **FSW nie jest ogólnym obserwatorem zapisów
przez trzymany uchwyt** — to, czy coś zobaczy, zależy od wzorca zapisu piszącego. Dlatego
pytanie trzeba było zadać prawdziwemu launcherowi, a jeden przebieg zrobić bez odpytywania.

### Przebiegi

| Run | Co | Monitor | Lock DAT (sonda) | Zapisy (FSW) | Zdarzenia FSW |
|---|---|---|---|---|---|
| 1 | **realny update 47.2 → 49.7** | FSW + sonda + stat | **12:46:29.1 → 12:48:21.7 = ~112.7 s** | 12:48:17.714 → 21.623 = **3.9 s** | 44 (lastwrite 20, other 20, size 4) w 19 momentach, **max przerwa 0.94 s** |
| 2 | forced-downgrade (tylko angielski DAT) | **tylko FSW** | — (sonda wyłączona) | 12:54:18.899 → 22.167 = 3.3 s | 42 (20/18/4) w 13 momentach, max przerwa 0.94 s |
| 3 | forced-downgrade | FSW + sonda + stat | 12:56:04.2 → 08.5 = **~4.4 s** | 12:56:05.127 → 08.475 = 3.3 s | 41 (20/17/4) w 10 momentach, max przerwa 0.92 s |
| 4 | **zwykły start, bez update'u** | FSW + sonda + stat | nigdy LOCKED | jeden moment 12:57:24.805 | 6 (lastwrite 4, other 2), **0 × size** |

Wszystkie przebiegi: 0 zdarzeń `Error` (zero overflow bufora 64 KiB). Kontrole negatywne
(monitor bez launchera, 15–26 s; ekran logowania po update, 16 s – 3 min; zamknięcie launchera
przez `CloseMainWindow`) — **0 zdarzeń**. Skoki rozmiaru identyczne co do bajta w runach 1–3:
1,876,926,448 → 1,883,420,500 → 1,892,866,524 → 1,903,352,284 → **1,906,498,012** (+28.2 MiB).

**Anatomia runu 1** (z `PatchClient.log`, t0 ≈ 12:46:25.6): launcher najpierw podmienia własne
binarki (4 file patches, 20.4 MB), potem łata DAT-y **po kolei, każdy w całości** (log jest
ring-bufferem — początek przepadł; w kolejce był też m.in. gamelogic): …mesh →
general → anim → sound → highres → surface → cell_1…cell_14 → **local_English na końcu** (razem z trzema małymi map_*)
(t = 110.8–114.7 s, 294 iteracje 26560–26875, 37.4 MB). Angielski DAT jest **zablokowany od
t ≈ 3.4 s**, ale przez **108.6 s nikt do niego nie pisze** — size i mtime (stat co 1 s) stoją,
FSW milczy, i ta cisza jest prawdziwa (self-test: stat widzi mtime zapisów w miejscu nawet przy
trzymanym uchwycie). Lock schodzi 0.1 s po ostatnim zapisie, czyli razem z końcem całego
łatania („Data patching complete. 5897 iterations applied, 1038627303 bytes downloaded").

### Odpowiedzi na pytania #660

- **(a) pierwsze zdarzenie FSW vs pierwszy LOCKED:** run 1 — **108.6 s** locka bez żadnego
  zapisu przed pierwszym zdarzeniem (inne DAT-y w łataniu); run 3 (tylko angielski) — ~1 s.
  E2 („faza pobierania zostawia DAT wolny") **nie uogólnia się na update wielu DAT-ów**: tam
  launcher łapie angielski DAT na starcie łatania i trzyma go do końca.
- **(b) zdarzenia W TRAKCIE locka czy dopiero na końcu: W TRAKCIE.** Co ≤ 0.94 s przez cały
  burst, we wszystkich trzech przebiegach — także w runie 2, gdzie nikt nie odpytywał pliku,
  więc to launcher sam wywołuje powiadomienia (jego wzorzec zapisu — rozszerzanie pliku/flush
  per partia iteracji; wywołań API nie śledziliśmy). W runie 1 z tyknięciem naszej sondy
  pokrywają się tylko zdarzenia z 12:48:18.44.
- **(c) zapis przy starcie vs burst apply:** różnica potwierdzona z punktu widzenia FSW —
  start = **jeden moment, tylko lastwrite/other, 0 × size**, DAT ani razu LOCKED przy sondzie
  co 1 s; apply = **10–19 momentów przez 3+ s z 4 zdarzeniami size**. Zastrzeżenie: rozmiar
  nie jest ogólną sygnaturą applyu — **49.4 (2026-08-22, wyżej) był realnym update'em bez
  zmiany rozmiaru DAT**, więc taki apply dałby same zdarzenia lastwrite/other.

### Werdykt dla gałęzi B (spec 0012 Tier 1, reguła 4)

1. **Quiesce jest obserwowalny:** obecny launcher zgłasza zapisy w trakcie locka, więc 30 s
   ciszy FSW naprawdę znaczy „30 s bez zapisów do angielskiego DAT".
2. **Ale cisza na angielskim DAT nie znaczy „update skończony":** w runie 1 DAT był trzymany
   108.6 s bez zapisu, w środku update'u — bramka 30 s byłaby spełniona 3.6× z rzędu, zanim
   launcher w ogóle dotknął angielskiego DAT. W runie 1 B i tak by się nie uzbroił (reguła 3
   wymaga wcześniej zaobserwowanego burstu apply), **ale tylko dlatego, że angielski DAT
   przyszedł na końcu** — kolejności nie kontrolujemy i nie widzieliśmy innej.
3. **B nie był potrzebny w żadnym zaobserwowanym update:** E2, 49.4, run 1 i obie powtórki —
   DAT zwalnia się w chwili końca łatania, a ekran logowania ma wolny DAT (gałąź A).

⇒ **rekomendacja: B jako opt-in (domyślnie wyłączony).** Reguła 4 mówi tylko „default-on
wyłącznie, jeśli quiesce jest obserwowalny" — jest, ale nie oznacza końca update'u; to nowy
powód spoza reguły, więc decyzja należy do ownera (#566). Jeśli kiedyś B zostanie włączony,
dodatkowa przesłanka:
linia „Data patching complete" w `%LOCALAPPDATA%\The Lord of the Rings Online\PatchClient.log`
— to sygnał końca CAŁEGO łatania, stan pliku (zgodny z filozofią Tier 1), w przeciwieństwie do
ciszy na jednym DAT. Uwaga: log jest ring-bufferem (rotacja do `PatchClient.1.old`) o
nieudokumentowanym formacie — heurystyka, nie kontrakt. Ostateczne cięcie: #566.

**Wkład do reguły 2 (wyzwalacz pętli, decyzja w #566):** „burst, który zmienił rozmiar DAT" nie
złapie applyu bez zmiany rozmiaru (49.4). Pewniejszy dyskryminator: po każdym obcym burście
zapisu snapshot E5 (open + `GetSubfileSizes`, 0.14 s) porównany z tym sprzed burstu — zapis przy
starcie zostawia go nietkniętym (E5, 49.4, run 4), apply go rusza (każdy realny update do tej
pory). FSW zostaje tanim „ktoś pisał — sprawdź", snapshot rozstrzyga.

### Wyniki uboczne

- **E5 na trzecim realnym update (47.2 → 49.7, skumulowany):** 306,370 → 311,970 SubFile'ów
  (277,084 → 282,190 text); **iteration changed 1,223** (1,188 text), size changed 998 (969
  text) — **size przegapił 225 (18%)**; added 6,073 (5,579 text); removed 473. `any changed`
  = `iteration changed` po raz trzeci. Koszt: open + `GetSubfileSizes` 135–148 ms.
- **Forced-downgrade zbiega per SubFile, nie bajt w bajt:** snapshot E5 po powtórce = po
  realnym update (0/0/0/0/0), rozmiar identyczny, ale każdy przebieg daje inny SHA256 (run 1
  `476F1F5F…E945`, run 2 `FBABEBA1…38C1`, run 3 `B30081DF…064E`); real vs run 3 = 21,433
  bajtów w 739 blokach 4 KiB (pola 2-bajtowe, od ~1.0 GB w głąb pliku). **Testy oparte na
  symulatorze (#567) porównują stan przez snapshot E5 albo eksport, nigdy hashem pliku.**
- **Kontrola negatywna E5:** po powtórce ↔ po zwykłym starcie launchera = 0 we wszystkich
  kolumnach (czwarte potwierdzenie, że zapis przy starcie nie rusza metadanych SubFile'i).
- **Bonus #660 — nasz pełny `patch` pod watcherem:** syntetyczny pełny korpus z eksportu 49.7
  (`PL ` + oryginał, `source_digest` zachowany — 806,120 wierszy, jak w E3) na **kopii** DAT 49.7,
  monitor tylko-FSW: **806,120 / 806,120 zapisanych** (0 `source moved`, 0 bez digestu) w **10.2 s**
  wall clock; watcher zgłosił **22 zdarzenia** (size 6, lastwrite 8, other 8) w 8 momentach przez
  5.9 s, max przerwa 1.3 s, **0 overflow**. Rozmiar rósł skokami po dokładnie 1 MiB (+6 MiB).
  Obciążenie, które pętla Tier 1 musi odfiltrować jako własne zapisy, jest więc znikome — bufor
  64 KiB jest daleko od przepełnienia.
- **Gotcha narzędziowa:** w Windows PowerShell 5.1 skrypt z `[CmdletBinding()]` odpalony przez
  `powershell -File` widzi pusty `$PSScriptRoot` w domyślnych wartościach parametrów (dotyczy
  E5 — odpalany jak w jego nagłówku, `.\e5-….ps1` w bieżącej sesji, działa). E6 wylicza ścieżki
  w ciele skryptu.

## Pliki intel (gitignored `intel/update-49/`)

DAT backupy 48.8 + 49 + write-test (po ~1.76 GB), pełne exporty 48.8/49 (82.6/83.1 MB), pełny
diff (1.76 MB), snapshoty polish.txt (resident pre-LEGAL-08 + repo post-LEGAL-08) i version file,
snapshoty E5 (`e5-*.csv` + `.meta.txt`, ~7 MB każdy: after-patch, after-real-update-49.3,
after-plain-launch, backup-48.8-offline + listy zmienionych tekstowych FileId).
Dołożone 2026-08-22 przy 49.4: backup DAT `client_local_English.49.3.pre-49.4.dat` (1.76 GB —
domyka symulator forced-downgrade dla cyklu 49.3→49.4, przydatny w #566/#567) oraz snapshoty
`e5-pre-49.4-*` i `e5-after-real-update-49.4-*`.
Committed tutaj: BASELINE.md + RESULTS.md (synthetic). Kopia robocza `data/exported.txt` =
**export 49.4 z 2026-08-22** (801,179 fragmentów w 281,410 plikach tekstowych, 97.77 MB,
SHA256 `DF2A12A3…D45F`) — deliverable do importu w TMS. Uwaga: to **nowy 7-kolumnowy format
z `source_digest`** (ADR-0047), a nie 6-kolumnowy format eksportu 49.1; stąd +14.7 MB przy
śladowym przyroście treści (~18 B/wiersz). Poprzedni export 49.1 (SHA256 `56F6D046…32B00`,
6 kolumn) zachowany jako `intel/update-49/export-49.txt`.

Dołożone 2026-10-10 przy E6 (druga maszyna ownera, gitignored **`intel/update-49.7/`**): backupy
DAT `client_local_English.pre-update-2026-04-18.dat` (stan 47.2, 1.88 GB — symulator
forced-downgrade dla skumulowanego 47.2→49.7) i `client_local_English.49.7.dat` (realny update,
SHA256 `476F1F5F…E945` — baseline przed Wolves of Mordor, 2026-10-28; jeśli wcześniej wyjdzie
49.8, baseline trzeba zdjąć na nowo), snapshoty E5 (`e5-pre-update-*`, `e5-after-real-update-49.7-*`,
`e5-after-replay-run2/3-*`, `e5-after-plain-launch-*`), logi E6 (`e6-run1…4-*.log`,
`e6-driver.log` + `e6-driver.ps1`) i kopie logów launchera po każdym przebiegu
(`launcher-logs-*`). **Eksport 49.7** zrobiony offline z backupu (`export -d <backup 49.7>`, 7.8 s,
bez elevacji): `export-49.7.txt` — **806,120 fragmentów w 282,190 plikach tekstowych**, 98.4 MB,
7 kolumn z `source_digest`, SHA256 `92F7034E…A53D` (vs export 49.4: +4,941 fragmentów, +780
plików) — deliverable do importu w TMS.
