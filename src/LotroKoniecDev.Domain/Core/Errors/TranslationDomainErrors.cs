using LotroKoniecDev.Domain.Core.BuildingBlocks;

namespace LotroKoniecDev.Domain.Core.Errors;

public static partial class DomainErrors
{
    public static class Translation
    {
        public static Error FileNotFound(string path) =>
            Error.NotFound("Translation.FileNotFound", $"Translation not found: {path}");

        public static Error InvalidFormat(string line) =>
            DomainErrors.InvalidFormat("Translation", line);

        public static Error ParseError(string line, string message) =>
            Error.Validation("Translation.ParseError", $"Error parsing line '{line}': {message}");

        public static Error NoTranslations =>
            Error.Validation("Translation.NoTranslations", "No translations to apply.");
    }

    public static class Export
    {
        public static Error CannotCreateOutputFile(string path, string message) =>
            IoError("Export", "CannotCreateOutput", $"'{path}': {message}");
    }
}
