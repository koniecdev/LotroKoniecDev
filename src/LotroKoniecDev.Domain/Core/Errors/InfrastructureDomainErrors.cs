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
                "LOTRO client is running. Close the game before patching.");

        public static Error NoWriteAccess(string path) =>
            IoError("DatFileLocation", "NoWriteAccess", $"'{path}'. Run as Administrator.");
    }

    public static class DatFileProtection
    {
        public static Error ProtectFailed(string path, string message) =>
            IoError("DatFileProtection", "ProtectFailed", $"'{path}': {message}");

        public static Error UnprotectFailed(string path, string message) =>
            IoError("DatFileProtection", "UnprotectFailed", $"'{path}': {message}");
    }

    public static class GameLaunch
    {
        public static Error LauncherNotFound(string path) =>
            NotFound("GameLaunch", $"TurbineLauncher.exe at '{path}'");

        public static Error LaunchFailed(string message) =>
            OperationFailed("GameLaunch", message);
    }

    public static class GameUpdateCheck
    {
        public static Error NetworkError(string message) =>
            IoError("GameUpdateCheck", "NetworkError", message);

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
