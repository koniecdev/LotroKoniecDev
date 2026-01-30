using LotroKoniecDev.Domain.Core.BuildingBlocks;

namespace LotroKoniecDev.Domain.Core.Errors;

public static partial class DomainErrors
{
    public static class DatFile
    {
        public static Error NotFound(string path) =>
            DomainErrors.NotFound("DatFile", path);

        public static Error CannotOpen(string path) =>
            IoError("DatFile", "CannotOpen", path);

        public static Error ReadError(int fileId, string message) =>
            IoError("DatFile", "ReadError", $"file {fileId}: {message}");

        public static Error WriteError(int fileId, string message) =>
            IoError("DatFile", "WriteError", $"file {fileId}: {message}");
    }

    public static class SubFile
    {
        public static Error NotFound(int fileId) =>
            DomainErrors.NotFound("SubFile", $"subfile {fileId} in DAT archive");

        public static Error NotTextFile(int fileId) =>
            Error.Validation("SubFile.NotTextFile", $"File {fileId} is not a text file.");

        public static Error ParseError(int fileId, string message) =>
            OperationFailed("SubFile", $"Error parsing subfile {fileId}: {message}");
    }

    public static class Fragment
    {
        public static Error NotFound(int fileId, long fragmentId) =>
            DomainErrors.NotFound("Fragment", $"fragment {fragmentId} in file {fileId}");
    }
}
