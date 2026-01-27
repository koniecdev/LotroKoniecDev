using LotroKoniecDev.Domain.Core.BuildingBlocks;

namespace LotroKoniecDev.Domain.Core.Errors;

/// <summary>
/// Contains all domain-specific errors for the LOTRO patcher.
/// </summary>
public static class DomainErrors
{
    public static class DatFile
    {
        public static Error NotFound(string path) =>
            Error.NotFound("DatFile.NotFound", $"DAT file not found: {path}");

        public static Error CannotOpen(string path) =>
            Error.IoError("DatFile.CannotOpen", $"Cannot open DAT file: {path}");

        public static Error ReadError(int fileId, string message) =>
            Error.IoError("DatFile.ReadError", $"Error reading file {fileId}: {message}");

        public static Error WriteError(int fileId, string message) =>
            Error.IoError("DatFile.WriteError", $"Error writing file {fileId}: {message}");
    }

    public static class SubFile
    {
        public static Error NotFound(int fileId) =>
            Error.NotFound("SubFile.NotFound", $"Subfile {fileId} not found in DAT archive.");

        public static Error NotTextFile(int fileId) =>
            Error.Validation("SubFile.NotTextFile", $"File {fileId} is not a text file.");

        public static Error ParseError(int fileId, string message) =>
            Error.Failure("SubFile.ParseError", $"Error parsing subfile {fileId}: {message}");
    }

    public static class Fragment
    {
        public static Error NotFound(int fileId, long fragmentId) =>
            Error.NotFound("Fragment.NotFound", $"Fragment {fragmentId} not found in file {fileId}.");
    }

    public static class Translation
    {
        public static Error FileNotFound(string path) =>
            Error.NotFound("Translation.FileNotFound", $"Translation file not found: {path}");

        public static Error InvalidFormat(string line) =>
            Error.Validation("Translation.InvalidFormat", $"Invalid translation format: {line}");

        public static Error ParseError(string line, string message) =>
            Error.Validation("Translation.ParseError", $"Error parsing line '{line}': {message}");

        public static Error NoTranslations =>
            Error.Validation("Translation.NoTranslations", "No translations to apply.");
    }

    public static class Export
    {
        public static Error CannotCreateOutputFile(string path, string message) =>
            Error.IoError("Export.CannotCreateOutput", $"Cannot create output file '{path}': {message}");
    }

    public static class Backup
    {
        public static Error CannotCreate(string path, string message) =>
            Error.IoError("Backup.CannotCreate", $"Cannot create backup at '{path}': {message}");

        public static Error CannotRestore(string path, string message) =>
            Error.IoError("Backup.CannotRestore", $"Cannot restore from backup '{path}': {message}");
    }
}
