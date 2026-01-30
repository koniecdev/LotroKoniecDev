# Primitives Layer

Lowest-level shared constants and enumerations. No dependencies on any other project.

## Contents

```
Constants/
  DatFileConstants.cs    TextFileMarker = 0x25 (identifies text subfiles in DAT)
                         PieceSeparator = "<--DO_NOT_TOUCH!-->" (argument placeholder in text)
Enums/
  DatFileSource.cs       StandingStoneGames, Steam, Registry, DiskScan, LocalFallback
  ErrorType.cs           Validation, NotFound, Failure, IoError
```
