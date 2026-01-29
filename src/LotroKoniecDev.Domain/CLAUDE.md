# Domain Layer

Pure domain layer - no dependencies on Application or Infrastructure. Only depends on Primitives.

## Structure

```
Core/
  BuildingBlocks/
    Error.cs          Sealed error class (Code, Message, Type). Factory: Validation(), NotFound(), Failure(), IoError()
    ValueObject.cs    Base class for structural equality via GetAtomicValues()
  Monads/
    Result.cs         Result monad: Result (no value) and Result<T> (with value)
  Extensions/
    ResultExtensions.cs   Functional: Map, Bind, OnSuccess, OnFailure, Match, ToResult, Combine
  Errors/
    DomainErrors.cs   Static factory per domain: DatFile, SubFile, Fragment, Translation, Export, Backup, DatFileLocation
  Utilities/
    VarLenEncoder.cs  Variable-length int encoding (1 byte: 0-127, 2 bytes: 128-32767)
Models/
  Translation.cs     Record: FileId, GossipId, Content, ArgsOrder, ArgsId
  Fragment.cs        Text fragment with pieces[], arg references, arg strings. Has Parse()/Write() for binary serialization
  SubFile.cs         DAT subfile: FileId, Version, Fragments collection. Has Parse()/Serialize() for binary I/O
```

## Key Conventions

- All operations return `Result` or `Result<T>`, never throw for domain errors
- `Error.None` is the sentinel for no-error state
- `Result<T>.Value` throws if `IsFailure` - always check first
- Implicit conversion: `T` -> `Result<T>` (success)
- Models use binary serialization (not JSON) matching DAT file format
- Fragment pieces are UTF-16LE strings with length-prefix encoding
- VarLenEncoder: values 0-127 use 1 byte, 128-32767 use 2 bytes (high bit flag)
