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

        public static Error ReadOnly(string path) =>
            Error.IoError("DatFile.ReadOnly",
                $"The game file '{path}' is marked read-only, so the patcher cannot write to it. " +
                "To allow writing, right-click the file, choose Properties, clear the Read-only check box and click OK.");

        public static Error WriteAccessDenied(string path) =>
            Error.IoError("DatFile.WriteAccessDenied",
                $"Windows does not let the patcher write to the game file '{path}'. " +
                "Run the patcher as administrator. " +
                "If it already runs as administrator, check the file's permissions on the Security tab of its Properties. " +
                "An antivirus or another security program can also block writing to the file.");

        public static Error InUse(string path) =>
            Error.IoError("DatFile.InUse",
                $"Another program has the game file '{path}' open, so the patcher cannot write to it. " +
                "Close every program that may be using the file, including the game.");

        public static Error CannotOpenForWriting(string path, string? reason) =>
            Error.IoError("DatFile.CannotOpenForWriting",
                string.IsNullOrWhiteSpace(reason)
                    ? $"The patcher cannot open the game file '{path}' for writing."
                    : $"The patcher cannot open the game file '{path}' for writing: {reason}");
    }

    public static class SubFile
    {
        public static Error NotFound(int fileId) =>
            DomainErrors.NotFound("SubFile", $"subfile {fileId} in DAT archive");

        public static Error NotTextFile(int fileId) =>
            Error.Validation("SubFile.NotTextFile", $"File {fileId} is not a text file.");

        public static Error ParseError(int fileId, string message) =>
            OperationFailed("SubFile", $"Error parsing subfile {fileId}: {message}");

        public static Error NotWritableUnchanged(int fileId) =>
            OperationFailed(
                "SubFile",
                $"Subfile {fileId} holds data the patcher cannot write back exactly as it was read, so it was left untouched.");
    }

    public static class Fragment
    {
        public static Error NotFound(int fileId, long fragmentId) =>
            DomainErrors.NotFound("Fragment", $"fragment {fragmentId} in file {fileId}");
    }
}
