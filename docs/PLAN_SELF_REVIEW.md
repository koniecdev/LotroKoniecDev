# Self-Review: PROJECT_PLAN.md

Brutalna analiza planu na bazie rzeczywistego kodu. Linia po linii sprawdzone
co plan zakłada vs co kod faktycznie robi.

---

## KRYTYCZNE — Plan jest błędny lub niekompletny

### 1. `net10.0-windows` + `x86` — Web API i Blazor nie zbudują się

**Problem:** `Directory.Build.props` ustawia globalnie:
```xml
<TargetFramework>net10.0-windows</TargetFramework>
<PlatformTarget>x86</PlatformTarget>
```

To dziedziczą **WSZYSTKIE** projekty w solution. Plan zakłada że Web API
(M3) będzie cross-platform `net10.0`, a Blazor WASM (M4) wymaga `net10.0`
obligatoryjnie — nie może być `-windows`.

**Ale jest gorzej.** Infrastructure project jest referencją i dla CLI i
(w planie) dla Web API. Infrastructure ma `AllowUnsafeBlocks=true` i native
DLLs (datexport.dll, msvcp71.dll, zlib1T.dll). Jeśli Infrastructure to
`net10.0-windows` + x86, to Web API jako `net10.0` **nie może go referencjonować**
— TFM mismatch.

**Co trzeba zrobić (plan tego nie mówi):**

1. Wyciągnąć TFM/PlatformTarget z `Directory.Build.props` — ustawiać per-project
2. Rozszczepić Infrastructure na dwa projekty:
   - `LotroKoniecDev.Infrastructure.DatFile` — `net10.0-windows`, x86, P/Invoke,
     native DLLs. Referencja tylko z CLI.
   - `LotroKoniecDev.Infrastructure.Persistence` — `net10.0`, EF Core, SQLite,
     repozytoria. Referencja z CLI i Web API.
3. Domain, Application, Primitives → `net10.0` (bez `-windows`). Nie używają
   żadnych Windows API, więc to safe.
4. CLI → `net10.0-windows` + x86 (potrzebuje datexport.dll)
5. WebApi → `net10.0` (cross-platform)
6. Blazor WASM → `net10.0` (browser runtime)

**To nie jest "risk note" — to wymagany refaktoring PRZED M2/M3.**
Plan powinien mieć dedykowany issue "Restructure TFMs and split Infrastructure".

**Referencja w kodzie:** `Directory.Build.props:3-4`,
`LotroKoniecDev.Infrastructure.csproj:16-35` (native DLLs).

---

### 2. Quest Browser oparty na fantazji — brak danych o questach w exporcie

**Problem:** Plan mówi (M4 #40): "Create Quest Browser view (search by quest
title)". Plan zakłada (M2 #13): `QuestTitle TEXT` w schemacie DB. Issue #23:
"Extract quest titles from content heuristics".

Ale co jest w exporcie? `Exporter.cs:164`:
```csharp
writer.WriteLine($"{fileId}||{fragmentId}||{text}||{argsOrder}||{argsId}||1");
```

To jest surowy tekst z fragmentu DAT. Nie ma:
- Nazwy questa
- Kategorii (quest/item/NPC/UI)
- Powiązania FileId → quest name
- Żadnych metadanych oprócz FileId + GossipId + tekst

FileId to bitmaskowany ID (high byte = 0x25 dla tekstu). GossipId to
fragment wewnątrz subfile. Wiele GossipIds w jednym FileId może należeć
do jednego questa, ale też do wielu.

**Heurystyka "extract quest title from content"** — nie zadziała. Treść to
np. `'Mamy trop, <--DO_NOT_TOUCH!-->! Szlak czerwonych kwiatów...'` — to jest
dialog NPC, nie tytuł questa.

**Co faktycznie da się zrobić:**
- **Szukanie po treści** — pełen tekst, angielski i polski. To działa i jest
  przydatne. Użytkownik pamięta fragment tekstu questa, wpisuje, znajduje.
- **Grupowanie po FileId** — wszystkie fragmenty z tego samego FileId.
  Nie daje nazwy questa, ale daje "wszystkie teksty z tego samego pliku".
- **Ręczne tagowanie** — tłumacz sam oznacza wpisy jako "Quest: Goblin Slayer".
- **Zewnętrzne dane** — scraping LOTRO wiki, API lotro-wiki.com, ale to zupełnie
  inny scope.

**Korekta planu:**
- Usuń `QuestTitle` z schematu (lub zostaw jako nullable + ręczne tagowanie)
- Issue #40 → "Create File Browser view (group by FileId, full-text search)"
- Issue #23 → "Create exported text parser (parse format, extract FileId groups)"
- Nie obiecuj "search by quest title" — obiecuj "full-text search"

---

### 3. `Action<int, int>? progress` — nie pasuje do MediatR

**Problem:** Oba interfejsy (`IExporter`, `IPatcher`) przyjmują callback:
```csharp
Result<ExportSummary> ExportAllTexts(string datFilePath, string outputPath,
    Action<int, int>? progress = null);
```

MediatR handler dostaje request i zwraca response. Nie ma mechanizmu progress
callback. Plan (issues #2, #3) nie adresuje tego.

**Opcje:**
a) `IProgress<T>` pattern — handler dostaje `IProgress<OperationProgress>` via DI.
   CLI rejestruje `ConsoleProgress`, Web API rejestruje `NoOpProgress` albo WebSocket.
b) Callback w request obiekcie — brzydkie, wiąże request z prezentacją.
c) Pominąć progress w handlerze — handler nie raportuje postępu, CLI/API dodaje
   swoją warstwę.
d) MediatR `INotification` — handler publishuje `ProgressNotification`, subskrybenci
   obsługują per-platform.

**Rekomendacja:** (a) — `IProgress<T>` w DI. Czyste, testowalne, per-platform.

**Brakujący issue:** "Design progress reporting pattern for MediatR handlers"
(przed #2 i #3).

---

### 4. Args reordering — NIE DZIAŁA w obecnym Patcherze

**Problem:** `Patcher.ProcessPatching()` linia 131:
```csharp
fragment.Pieces = translation.GetPieces().ToList();
```

Ustawia przetłumaczone fragmenty tekstu. Ale NIGDY nie reorderuje argumentów.

`Patcher.SaveSubFile()` linia 166:
```csharp
byte[] data = subFile.Serialize();
```

Wywołuje `Serialize()` BEZ parametrów reorderingu. `SubFile.Serialize()` ma
opcjonalne parametry `argsOrder`, `argsId`, `targetFragmentId` — ale Patcher
ich nie przekazuje.

`SubFile.ReorderArguments()` istnieje w kodzie (linia 109-116), ale nigdy
nie jest wywoływany przez Patcher.

**Efekt:** Pole `args_order` w pliku tłumaczeń jest:
- Parsowane przez `TranslationFileParser` → `Translation.ArgsOrder`
- Ignorowane przez `Patcher` — nigdy nie użyte
- Eksportowane przez `Exporter` → zawsze "1-2-3" (default order)

**Wyszedł karkołomny żart:**
- Format pliku ma 6 pól
- Parser czyta 5 (ignoruje `approved`)
- Patcher używa 2 (FileId, Content z GossipId). ArgsOrder i ArgsId → dead code.

**Impact na plan:**
- Web App będzie miał UI do "drag & drop args order" (#39)... który nic nie zmieni
  w grze bo Patcher to ignoruje.
- Plan M1 (MediatR refaktor) powinien zawierać issue: "Fix: wire args reordering
  in patch pipeline"
- Albo — jeśli args reordering nie jest potrzebny — usunąć z modelu i UI.

**Weryfikacja:** Sprawdź czy tłumaczenie z `2-1` args_order faktycznie zmienia
coś w grze. Jeśli nie — to dead feature od początku.

---

## WAŻNE — Plan jest niejasny lub mylący

### 5. Pole `approved` — dead code w parserze

**Stan faktyczny:**
- `Exporter.cs:164` — zawsze pisze `||1` na końcu linii
- `TranslationFileParser.cs:69-87` — czyta `parts[0..4]`, ignoruje `parts[5]`
- `Translation.cs` — nie ma property `IsApproved`
- `MinimumFieldCount = 5` — szóste pole opcjonalne

**Plan zakłada** approval jako feature: DB schema, filter, web app toggle.
To dobrze — ale plan musi powiedzieć wprost:

1. Dodać `IsApproved` do `Translation` modelu (lub nowego DB entity)
2. Zaktualizować `TranslationFileParser` żeby czytał 6. pole
3. Zaktualizować `Patcher` żeby pomijał niezatwierdzone (approved=0)
4. Migracja: wszystkie istniejące tłumaczenia → approved=1

**Brakujący issue** w M1 lub M2: "Implement approved field parsing and filtering"

---

### 6. IExporter / IPatcher — co z nimi po MediatR?

Plan jest niezdecydowany. Issue #9 mówi: "Optionally keep IExporter/IPatcher
registrations if handlers delegate to them. Or remove them if handlers contain
the logic directly."

**To musi być decyzja, nie opcja.** Dwie ścieżki:

**Ścieżka A: Handlers zastępują serwisy (rekomendacja)**
- `ExportTextsQueryHandler` zawiera logikę z `Exporter.ExportAllTexts()`
- `ApplyPatchCommandHandler` zawiera logikę z `Patcher.ApplyTranslations()`
- `IExporter`, `IPatcher` → usunięte
- `Exporter`, `Patcher` klasy → usunięte
- DI registration bez tych interfejsów
- Testy mockują `IDatFileHandler`, `ITranslationParser` (nie IExporter/IPatcher)

**Ścieżka B: Handlers delegują do serwisów**
- `ExportTextsQueryHandler` inject `IExporter`, wywołuje `ExportAllTexts()`
- Handler jest cienkim wrapperem
- IExporter/IPatcher zostają
- **Problem:** Handler jest zbędną warstwą. Zrobiłeś MediatR dispatch
  żeby... wywołać jedną metodę serwisu. Nie ma sensu.

**Prawidłowa odpowiedź:** Ścieżka A. Handler IS use case. Serwis znika.

Plan powinien to jasno mówić w issue #2, #3, i #8.

---

### 7. Dwa modele "Translation" — Domain DTO vs DB Entity

`Translation.cs` to `init`-only class bez zachowań (poza `GetPieces()`).
To nie jest domain entity — to DTO do pipeline DAT→patch.

Plan zakłada tabelę `Translations` w DB z polami: Id, FileId, GossipId,
PolishContent, ArgsOrder (string!), ArgsId, IsApproved, CreatedAt, UpdatedAt,
Notes.

**To jest INNY obiekt** niż `Translation` z Domain. Ma:
- `Id` (autoincrement) — Domain nie ma
- `CreatedAt`, `UpdatedAt` — Domain nie ma
- `Notes` — Domain nie ma
- `ArgsOrder` jako string "1-2-3" — Domain ma `int[]?`

**Plan powinien jasno powiedzieć:**
- `Domain.Models.Translation` → pozostaje jako DTO dla DAT pipeline
- `Infrastructure.Persistence.Entities.TranslationEntity` → nowy, DB entity
- Mapping między nimi w repository (entity → domain model i vice versa)
- Nie próbuj robić jednego modelu do wszystkiego

---

### 8. PreflightChecker — `Console.ReadLine()` w środku logiki

`PreflightChecker.cs:59-62`:
```csharp
Console.Write("Continue with patching anyway? (y/N): ");
string? answer = Console.ReadLine();
return string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase);
```

I analogicznie linia 71-76 dla "game is running".

**Plan issue #4** mówi: "PreflightReport provides all info for presentation
layer to decide/prompt user". Dobrze.

**Ale plan nie mówi wprost:** to jest BREAKING CHANGE w flow. Obecny kod:
```
Check → Prompt → Decision (w jednej metodzie)
```
Po MediatR:
```
Handler → Report (dane)
CLI → odczyt raportu → prompt → decision
```

To zmienia sygnaturę: z `Task<bool>` na `Task<Result<PreflightReport>>`.
CLI musi sam robić Console.ReadLine() na podstawie danych z raportu.

Issue #4 powinien zawierać:
- [ ] Zdefiniować `PreflightReport` (GameRunning, HasWriteAccess, UpdateDetected,
      PreviousVersion, CurrentVersion) — wszystkie dane do podjęcia decyzji
- [ ] Handler NIE interaguje z użytkownikiem — zero Console.*
- [ ] CLI czyta raport i sam decyduje o promptach

---

## MNIEJSZE ALE WARTE UWAGI

### 9. Docker — tylko Web API, NIE CLI

Issue #51 mówi "Docker Compose for Web API + DB". Ale plan wspomina też
o `full-workflow.bat` w kontekście Docker. CLI jest Windows x86 z native
DLLs — nie odpali się w Linux containerze.

**Korekta:** Docker = Web API + SQLite only. CLI = natywne Windows.
BAT files działają na hoście, nie w kontenerze.

### 10. `PatchSummary` — mutable `List<string>` w record

```csharp
public sealed record PatchSummary(
    int TotalTranslations,
    int AppliedTranslations,
    int SkippedTranslations,
    List<string> Warnings);  // ← mutable
```

MediatR responses powinny być immutable. Przy refaktoringu → `IReadOnlyList<string>`.

### 11. HttpClient jako Singleton — DNS problem

`InfrastructureDependencyInjection.cs:24-29`:
```csharp
services.AddSingleton<HttpClient>(_ => {
    HttpClient client = new HttpClient();
    ...
});
```

Anti-pattern. Web API i tak użyje `IHttpClientFactory`. Ale CLI też powinien
migrować na `IHttpClientFactory` przy okazji M1. Albo nie — to patcher,
nie serwer HTTP. HttpClient singleton jest OK dla krótkotrwałej CLI app.

### 12. `DatFileHandler` — Scoped + IDisposable + handle tracking

Handler jest Scoped, co znaczy jeden per DI scope. CLI tworzy scope per command.
Po MediatR — scope dalej tworzony w CLI, mediator resolve'owany w tym scope,
handler dostaje DatFileHandler — OK.

**Ale:** MediatR `AddMediatR()` z assembly scanning rejestruje handlery
jako Transient (domyślnie). Jeśli handler jest Transient i DatFileHandler
jest Scoped — handler dostanie scoped DatFileHandler via DI. To działa.

**Nie ma problemu** — ale warto zweryfikować lifetime rejestracji handlerów.

### 13. ExportCommand synchroniczny, PatchCommand async

```csharp
"export" => ExportCommand.Run(args, ...),         // sync
"patch" => await PatchCommand.RunAsync(...)         // async
```

MediatR obsługuje oba: `IRequest<T>` i `IRequestHandler` mogą być sync albo
async. Export handler może zostać sync. Patch handler (z async preflight
check) musi być async.

**Uwaga:** MediatR 12.x wymaga `Task<TResponse>` nawet dla sync handlerów.
Handler zwraca `Task.FromResult()` jeśli jest sync.

### 14. Brakujące pole w Translation — `approved` vs `content` parsing

Parser (`TranslationFileParser.cs:70`):
```csharp
string[] parts = line.Split([FieldSeparator], StringSplitOptions.None);
```

Separator to `||`. Ale co jeśli treść tłumaczenia zawiera `||`?

Np.: `620756992||1001||To jest tekst || z dziwnym formatem||NULL||NULL||1`

Parser splituje po `||` → dostaje 7 parts zamiast 6. `parts[2]` to
"To jest tekst ", a nie cały tekst.

**To jest potencjalny bug** w obecnym kodzie. Ale prawdopodobnie `||` nie
występuje w tekstach LOTRO, więc w praktyce nie strzela.

Plan zakłada web editor — użytkownik może wpisać `||` w tłumaczeniu.
DB model to naprawi (nie potrzebuje pipe-delimited format), ale CLI export
z bazy musi escapować `||` w treści.

---

## BRAKUJĄCE ISSUES w planie

Na podstawie review — te issues powinny istnieć ale ich nie ma:

| # | Issue | Milestone | Why |
|---|-------|-----------|-----|
| NEW-1 | Restructure TFMs: split Infrastructure, remove global net10.0-windows | M1 (BEFORE M2!) | Web API / Blazor won't build without this |
| NEW-2 | Design progress reporting pattern for MediatR handlers | M1 | IProgress<T> via DI, unblocks #2 and #3 |
| NEW-3 | Fix: wire args reordering in patch pipeline (or remove dead code) | M1 or backlog | Patcher ignores ArgsOrder/ArgsId |
| NEW-4 | Implement approved field: parse, filter, model property | M1 or M2 | Dead field in format, needed for DB approval feature |
| NEW-5 | Define Translation domain DTO vs TranslationEntity DB model | M2 | Two different models, plan doesn't specify |
| NEW-6 | Handle `\|\|` separator in translation content (escape/unescape) | M2 | Web editor allows arbitrary input |

---

## KOREKTY do istniejących issues

| Issue | Korekta |
|-------|---------|
| #2, #3 | Handlers REPLACE Exporter/Patcher, not wrap them. Delete IExporter/IPatcher. |
| #4 | Explicitly say: NO Console.ReadLine in handler. Return PreflightReport with all data. |
| #8 | Also delete IExporter, IPatcher interfaces (not just static commands). |
| #13 | Remove QuestTitle from schema. Replace with nullable Tags/Category (manual). |
| #23 | Don't extract quest titles. Parse format only. Group by FileId. |
| #40 | Rename: "File Browser (group by FileId, full-text search)" not "Quest Browser". |
| #43 | Add: also validate `||` in user input, warn/escape. |
| #51 | Explicitly: Docker for Web API + DB ONLY. CLI stays native Windows. |

---

## PODSUMOWANIE PRIORYTETÓW

**Zrób PRZED ruszeniem M1:**
- [ ] Decision: handlers replace vs wrap services (answer: replace)
- [ ] Decision: IProgress<T> pattern for progress reporting
- [ ] Decision: args reordering — fix or remove dead code?

**Zrób na starcie M1 (sprint 1):**
- [ ] Infrastructure split + TFM restructuring (blokuje M2/M3/M4)
- [ ] MediatR packages + first handler

**Zrób na starcie M2:**
- [ ] Define TranslationEntity vs Translation DTO mapping
- [ ] Implement approved field parsing
- [ ] Remove QuestTitle fantasy from schema

**Ogólna ocena planu:** Struktura milestones i podział na issues jest dobra.
Kierunek architektoniczny (MediatR CQRS + Web API + DB) jest słuszny.
Ale plan miał 4 krytyczne luki (TFM, quest title, progress, args) i 4 ważne
niejasności. Po korektach — plan jest solidny do wykonania.
