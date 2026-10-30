using LotroKoniecDev.Domain.Core.BuildingBlocks;

namespace LotroKoniecDev.Domain.Core.Errors;

public static partial class DomainErrors
{
    public static class Backup
    {
        public static Error CannotCreate(string path, string message) =>
            IoError("Backup", "CannotCreate", $"'{path}': {message}");

        public static Error CannotRestore(string path, string message) =>
            IoError("Backup", "CannotRestore", $"'{path}': {message}");
    }

    public static class DatFileLocation
    {
        public static Error NoneFound =>
            Error.NotFound("DatFileLocation.NoneFound",
                "No LOTRO installation found. Provide the DAT file path manually.");

        public static Error GameRunning =>
            OperationFailed("DatFileLocation",
                "LOTRO or its launcher is running. Close both before patching.");

        public static Error NoWriteAccess(string path) =>
            IoError("DatFileLocation", "NoWriteAccess", $"'{path}'. Run as Administrator.");
    }

    public static class GameLaunch
    {
        public static Error GameAlreadyRunning =>
            OperationFailed("GameLaunch", "LOTRO is already running. Close the game before launching.");

        public static Error LauncherNotFound(string path) =>
            NotFound("GameLaunch", $"LotroLauncher.exe at '{path}'");

        public static Error LaunchFailed(string message) =>
            OperationFailed("GameLaunch", message);

        public static Error RepatchFailed(string message) =>
            OperationFailed("GameLaunch", $"Re-patching translations after update failed: {message}");
    }

    public static class IterationSnapshot
    {
        public static Error ReadError(string path, string message) =>
            IoError("IterationSnapshot", "ReadError", $"'{path}': {message}");

        public static Error WriteError(string path, string message) =>
            IoError("IterationSnapshot", "WriteError", $"'{path}': {message}");
    }

    public static class GameUpdateCheck
    {
        public const string ResponseTooLargeCode = "GameUpdateCheck.ResponseTooLarge";

        public static Error NetworkError(string message) =>
            IoError("GameUpdateCheck", "NetworkError", message);

        public static Error ResponseTooLarge(long maxResponseBytes) =>
            Error.IoError(ResponseTooLargeCode,
                $"The response body exceeds the maximum allowed size of {maxResponseBytes} bytes.");

        public static Error VersionNotFoundInPage =>
            OperationFailed("GameUpdateCheck",
                "Could not find version information on the LOTRO release notes page.");

        public static Error GameUpdateRequired =>
            OperationFailed("GameUpdateCheck",
                "Game update is required");

        public static Error VersionFileError(string path, string message) =>
            IoError("GameUpdateCheck", "VersionFileError", $"'{path}': {message}");
    }
}
