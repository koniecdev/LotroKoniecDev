using LotroKoniecDev.Domain.Core.BuildingBlocks;

namespace LotroKoniecDev.Domain.Core.Errors;

public static partial class DomainErrors
{
    /// <summary>The desktop app's own copies of the translation file and its patch state (#42).</summary>
    public static class DesktopPatchWorkspace
    {
        public const string SourceNotFoundCode = "DesktopPatchWorkspace.NotFound";

        public const string SourceUnreadableCode = "DesktopPatchWorkspace.SourceUnreadable";

        public const string FileSystemErrorCode = "DesktopPatchWorkspace.FileSystemError";

        public static Error SourceNotFound(string path) =>
            Error.NotFound(SourceNotFoundCode, $"The translation file '{path}' does not exist.");

        /// <summary>The file exists, but it cannot be opened: another program locks it, or access is denied.</summary>
        public static Error SourceUnreadable(string path, string message) =>
            Error.IoError(SourceUnreadableCode, $"The translation file '{path}' cannot be read: {message}");

        public static Error FileSystemError(string path, string message) =>
            Error.IoError(FileSystemErrorCode, $"'{path}': {message}");
    }
}
