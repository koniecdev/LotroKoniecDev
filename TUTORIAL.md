# LOTRO Polish Patcher - Tutorial

## 1. Architektura systemu

```
┌─────────────────────────────────────────────────────────────┐
│                     Twoja aplikacja C#                       │
├─────────────────────────────────────────────────────────────┤
│  TranslationFile.txt ──► Parser ──► SQLite patch.db          │
│                                           │                  │
│                                           ▼                  │
│                                      Patcher                 │
│                                           │                  │
│                                           ▼                  │
│                               datexport.dll (P/Invoke)       │
│                                           │                  │
│                                           ▼                  │
│                            client_local_English.dat          │
└─────────────────────────────────────────────────────────────┘
```

---

## 2. Format pliku tłumaczeń (response z serwera)

### 2.1 Oryginalny format HTTP response

Serwer rosyjski zwraca dane w formacie **plain text**:
- Linie oddzielone przez `\r\n` (CRLF)
- Pola w linii oddzielone przez `||`

### 2.2 Struktura pojedynczej linii

```
file_id||gossip_id||content||args_order||args_id||approved
```

| Pole | Typ | Opis | Przykład |
|------|-----|------|----------|
| `file_id` | int | ID pliku w .dat (hex 0x25XXXXXX) | `620756992` |
| `gossip_id` | int | ID fragmentu tekstu | `268439552` |
| `content` | string | Przetłumaczony tekst | `"Witaj w grze!"` |
| `args_order` | string | Kolejność argumentów (1-indexed) lub puste | `"1-2-3"` lub `""` |
| `args_id` | string | ID argumentów lub `NULL` | `"1-2-3"` lub `"NULL"` |
| `approved` | string | Status zatwierdzenia | `"1"` lub `"0"` |

### 2.3 Przykładowy plik tłumaczeń

```
620756992||268439552||Witaj w Śródziemiu!||NULL||NULL||1
620756992||268439553||Twoja postać: <--DO_NOT_TOUCH!-->||1||1||1
620756992||268439554||Masz <--DO_NOT_TOUCH!--> sztuk złota i <--DO_NOT_TOUCH!--> srebra.||1-2||1-2||1
620757000||100001||Zaakceptuj||NULL||NULL||1
620757000||100002||Anuluj||NULL||NULL||1
620757000||100003||Poziom <--DO_NOT_TOUCH!-->: <--DO_NOT_TOUCH!-->||2-1||1-2||1
```

### 2.4 Specjalny separator w content

Tekst `<--DO_NOT_TOUCH!-->` to placeholder na dynamiczne argumenty (imię gracza, liczby, itp.)

**Oryginalny tekst w grze:**
```
"Level {0}: {1}"
```

**Tłumaczenie z argumentami:**
```
content = "Poziom <--DO_NOT_TOUCH!-->: <--DO_NOT_TOUCH!-->"
args_order = "2-1"   // zamień kolejność: najpierw arg 2, potem arg 1
args_id = "1-2"      // ID argumentów
```

**Wynik w grze:**
```
"Poziom [wartość arg 2]: [wartość arg 1]"
```

### 2.5 Zasady args_order i args_id

| Scenariusz | args_order | args_id |
|------------|------------|---------|
| Brak argumentów | `""` lub `"Null"` | `"NULL"` lub `"Null"` |
| Argumenty bez zmiany kolejności | `"1-2-3"` | `"1-2-3"` |
| Argumenty ze zmianą kolejności | `"2-1-3"` | `"1-2-3"` |

**Ważne:** args_order jest 1-indexed (zaczyna się od 1, nie od 0)

---

## 3. Format bazy SQLite (patch.db)

### 3.1 Schemat tabeli

```sql
CREATE TABLE text_files (
    file_id INTEGER,
    gossip_id INTEGER,
    content TEXT,
    args_order TEXT,
    args_id TEXT
);
```

### 3.2 Konwersja z pliku tekstowego do SQLite

```
Plik tekstowy:
620756992||268439552||Witaj!||NULL||NULL||1

↓ (parsowanie)

INSERT INTO text_files VALUES (620756992, 268439552, 'Witaj!', 'Null', 'Null');
```

**Uwaga:** W bazie SQLite używamy `'Null'` (string) zamiast `'NULL'`.

---

## 4. Format binarny plików .dat

### 4.1 Struktura SubFile (plik tekstowy w .dat)

```
┌────────────────────────────────────────┐
│ file_id          (4 bytes, uint32 LE)  │
│ unknown_1        (4 bytes)             │
│ unknown_2        (1 byte)              │
│ num_fragments    (1-2 bytes, varlen)   │
├────────────────────────────────────────┤
│ Fragment 1                             │
│ Fragment 2                             │
│ ...                                    │
│ Fragment N                             │
└────────────────────────────────────────┘
```

### 4.2 Struktura Fragment

```
┌────────────────────────────────────────┐
│ fragment_id      (8 bytes, uint64 LE)  │
│ num_pieces       (4 bytes, uint32 LE)  │
├────────────────────────────────────────┤
│ Piece 1:                               │
│   piece_size     (1-2 bytes, varlen)   │
│   piece_data     (piece_size * 2 bytes)│ <- UTF-16LE!
│ Piece 2: ...                           │
├────────────────────────────────────────┤
│ num_arg_refs     (4 bytes, uint32 LE)  │
│ arg_ref 1        (4 bytes each)        │
│ arg_ref 2: ...                         │
├────────────────────────────────────────┤
│ num_arg_string_groups (1 byte)         │
│ ArgStringGroup 1:                      │
│   num_strings    (4 bytes)             │
│   String 1:                            │
│     str_size     (1-2 bytes, varlen)   │
│     str_data     (str_size * 2 bytes)  │ <- UTF-16LE!
│   String 2: ...                        │
│ ArgStringGroup 2: ...                  │
└────────────────────────────────────────┘
```

### 4.3 Variable-length encoding (varlen)

Rozmiary (piece_size, str_size, num_fragments) używają specjalnego kodowania:

```csharp
int ReadVarLen(BinaryReader reader)
{
    int value = reader.ReadByte();
    if ((value & 0x80) != 0)
    {
        // Bit 7 ustawiony = 2 bajty
        value = ((value ^ 0x80) << 8) | reader.ReadByte();
    }
    return value;
}

void WriteVarLen(BinaryWriter writer, int value)
{
    if (value >= 0x80)
    {
        writer.Write((byte)((value >> 8) ^ 0x80));
        writer.Write((byte)(value & 0xFF));
    }
    else
    {
        writer.Write((byte)value);
    }
}
```

### 4.4 Identyfikacja plików tekstowych

Pliki tekstowe mają file_id zaczynający się od `0x25`:

```csharp
bool IsTextFile(int fileId) => (fileId >> 24) == 0x25;
```

---

## 5. datexport.dll - P/Invoke

### 5.1 Wymagane funkcje

```csharp
public static class DatExport
{
    private const string DLL = "datexport.dll";

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    public static extern int OpenDatFileEx2(
        int handle,
        [MarshalAs(UnmanagedType.LPStr)] string fileName,
        uint flags,
        out int didMasterMap,
        out int blockSize,
        out int vnumDatFile,
        out int vnumGameData,
        out uint datFileId,
        byte[] datIdStamp,      // 64 bytes
        byte[] firstIterGuid    // 64 bytes
    );

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetNumSubfiles(int handle);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    public static extern void GetSubfileSizes(
        int handle,
        int[] fileIds,
        int[] sizes,
        int[] iterations,
        int offset,
        int count
    );

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetSubfileVersion(int handle, int fileId);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    public static extern void GetSubfileData(
        int handle,
        int fileId,
        IntPtr buffer,
        int unknown,  // zawsze 0
        out int version
    );

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    public static extern int PurgeSubfileData(int handle, int fileId);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    public static extern int PutSubfileData(
        int handle,
        int fileId,
        IntPtr buffer,
        int unknown,  // zawsze 0
        int size,
        int version,
        int iteration,
        byte unknown2  // zawsze 0
    );

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    public static extern void Flush(int handle);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    public static extern void CloseDatFile(int handle);
}
```

### 5.2 Flagi OpenDatFile

```csharp
const uint DAT_OPEN_FLAGS = 130;  // Read + Write
```

---

## 6. Algorytm patchowania

```
1. Otwórz plik .dat (OpenDatFileEx2, flags=130)
2. Pobierz liczbę subplików (GetNumSubfiles)
3. Pobierz rozmiary wszystkich subplików (GetSubfileSizes)
4. Dla każdego wpisu w patch.db (posortowane po file_id):
   a. Jeśli nowy file_id:
      - Zapisz poprzedni zmodyfikowany subplik (jeśli był)
      - Wczytaj nowy subplik (GetSubfileData)
      - Sparsuj strukturę binarną (SubFile.Parse)
   b. Znajdź fragment o gossip_id
   c. Zamień pieces na nowy content (split by <--DO_NOT_TOUCH!-->)
   d. Jeśli args_order != Null: przetasuj arg_refs
5. Zapisz ostatni subplik (PurgeSubfileData + PutSubfileData)
6. Flush + CloseDatFile
```

---

## 7. Przykładowy przepływ

### Input: translations.txt
```
620756992||1001||Witaj!||Null||Null||1
620756992||1002||Poziom <--DO_NOT_TOUCH!-->||1||1||1
```

### Krok 1: Parsowanie do listy
```csharp
var translations = new List<Translation>
{
    new(620756992, 1001, "Witaj!", null, null),
    new(620756992, 1002, "Poziom <--DO_NOT_TOUCH!-->", new[]{1}, new[]{1}),
};
```

### Krok 2: Tworzenie patch.db
```sql
INSERT INTO text_files VALUES (620756992, 1001, 'Witaj!', 'Null', 'Null');
INSERT INTO text_files VALUES (620756992, 1002, 'Poziom <--DO_NOT_TOUCH!-->', '1', '1');
```

### Krok 3: Aplikacja patcha
```
Otwórz client_local_English.dat
Wczytaj SubFile 620756992
  Fragment 1001: pieces = ["Witaj!"]
  Fragment 1002: pieces = ["Poziom ", ""]  // split by separator
Zapisz SubFile 620756992
Zamknij .dat
```

---

## 8. Struktura projektu C#

```
LotroKoniecDev/
├── LotroKoniecDev.sln
├── src/
│   ├── DatExport.cs           // P/Invoke wrapper
│   ├── Models/
│   │   ├── Fragment.cs        // Model fragmentu
│   │   ├── SubFile.cs         // Model subpliku
│   │   └── Translation.cs     // Model tłumaczenia
│   ├── Parsers/
│   │   ├── TranslationFileParser.cs   // Parser pliku tłumaczeń
│   │   └── SubFileParser.cs           // Parser binarny
│   ├── Database/
│   │   └── PatchDatabase.cs   // SQLite operations
│   └── Patcher.cs             // Główna logika
├── translations/
│   └── polish.txt             // Twoje polskie tłumaczenia
└── lib/
    └── datexport.dll          // Skopiuj z oryginalnego projektu
```

---

## 9. Checklist implementacji

- [ ] Skopiować `datexport.dll` do projektu
- [ ] Zaimplementować P/Invoke wrapper (`DatExport.cs`)
- [ ] Zaimplementować model `Fragment` z Parse/Serialize
- [ ] Zaimplementować model `SubFile` z Parse/Serialize
- [ ] Zaimplementować parser pliku tłumaczeń
- [ ] Zaimplementować tworzenie SQLite patch.db
- [ ] Zaimplementować główną logikę patchera
- [ ] Przetestować na kopii `client_local_English.dat`

---

## 10. Ważne uwagi

1. **ZAWSZE rób backup** `client_local_English.dat` przed patchowaniem
2. Stringi w .dat są w **UTF-16LE** (2 bajty na znak)
3. `piece_size` to liczba **znaków**, nie bajtów (bajty = piece_size * 2)
4. Sortuj patche po `file_id` przed aplikacją (optymalizacja I/O)
5. Testuj na małej liczbie tłumaczeń najpierw
