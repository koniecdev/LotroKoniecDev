## 1. Tech Stack & Architecture
* **Stack:** C# 13, .NET 10.0.
* **Architecture:** Strict Clean Architecture (5 layers).
* **Dependency Rule:** Presentation (CLI) / Infrastructure -> Application -> Domain -> Primitives. NEVER violate this downward flow.

## Key Patterns

### Result Monad (Railway-Oriented Programming)
All operations return `Result` or `Result<T>`, never throw for domain errors - we use exceptions guards for programming errors.

## DAT Binary Format

```
SubFile (text, FileId high byte = 0x25):
  FileId (4B) | Unknown1 (4B) | Unknown2 (1B) | FragCount (VarLen)
  Fragment[]:
    FragmentId (8B ulong = GossipId) | PieceCount (int)
    Piece[]: VarLen length + UTF-16LE bytes
    ArgRefCount (int) | ArgRef[]: 4B each
    ArgStringGroupCount (byte) | Group[]: Count(int) + VarLen UTF-16LE strings

VarLen: 0-127 = 1 byte; 128-32767 = 2 bytes (high bit flag)
```

## Translation File Format

```
# Comments start with #
file_id||gossip_id||translated_text||args_order||args_id||approved
620756992||1001||Witaj w Srodziemiu!||NULL||NULL||1
620756992||1002||Tekst z <--DO_NOT_TOUCH!--> argumentem||1||1||1
```

- `<--DO_NOT_TOUCH!-->` = argument placeholder
- `args_order`: `NULL` or `1-2-3` (1-indexed in file, 0-indexed internally)
- `\r`, `\n` in content are unescaped by parser
- Results sorted by FileId then GossipId for sequential DAT I/O

## Game Update Detection

Two-source model:
1. **Forum checker** (proactive): scrapes lotro.com release notes, regex `Update\s+(\d+(?:\.\d+)*)\s+Release\s+Notes`
2. **DAT vnum** (confirmation): `OpenDatFileEx2()` -> `vnumGameData`

## DAT File Protection

`attrib +R` on `client_local_English.dat` — OS-level read-only, launcher cannot bypass.
Stronger than Russian project's `-disablePatch` flag - it intentionally blocks game updates.

## 3. Code Style & Syntax
* **Namespaces:** Use file-scoped namespaces.
* **Braces:** Use **Allman** style (opening brace on a new line).
* **Variables:** Use `var` only for anonymous types. Use explicit types otherwise.

## Reference: Russian Project (translate.lotros.ru)

Our project shares DNA: same datexport.dll, same 0x25 marker, same `||` format, same `<--DO_NOT_TOUCH!-->`.
If you really need it, See `docs/RUSSIAN_PROJECT_RESEARCH.md` for full analysis.

## Roadmap
If you really need it, see `docs/PROJECT_PLAN.md` for full plan with step-by-step execution guide.

# Cli Layer
CLI layer should serve as presentation layer - with fact in mind, that in the future, there will be WPF next to it.

#Infrastructure Layer
Infrastructure layer holds all the lotro dll's as well as classes for interacting with game data, external services, and so on.~~~~

# Application Layer
Application layer mediator handlers serves mainly as a orchestrators - technically it should call domain services for business logic.

# Domain Layer
Domain layer should follow Eric-Evans Domain-Driven Design principles.

# Primitives Layer
Lowest-level shared constants and enumerations. No dependencies on any other project.
