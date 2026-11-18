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

    /// <summary>
    /// The codes the desktop app turns into Polish texts (#42) are constants. Every error here is a
    /// Failure or NotFound type, so the CLI's exit codes do not depend on the codes.
    /// </summary>
    public static class GameLaunch
    {
        public const string AlreadyRunningCode = "GameLaunch.AlreadyRunning";

        public const string LauncherNotFoundCode = "GameLaunch.NotFound";

        public const string LaunchFailedCode = "GameLaunch.Failed";

        public const string RepatchFailedCode = "GameLaunch.RepatchFailed";

        public static Error GameAlreadyRunning =>
            Error.Failure(AlreadyRunningCode, "LOTRO is already running. Close the game before launching.");

        public static Error LauncherNotFound(string path) =>
            Error.NotFound(LauncherNotFoundCode, $"GameLaunch not found: LotroLauncher.exe at '{path}'");

        public static Error LaunchFailed(string message) =>
            Error.Failure(LaunchFailedCode, message);

        /// <summary>The patch before a launch, or the desktop app's Patch, could not apply the translations.</summary>
        public static Error RepatchFailed(string message) =>
            Error.Failure(RepatchFailedCode, $"Applying the translations failed: {message}");
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
