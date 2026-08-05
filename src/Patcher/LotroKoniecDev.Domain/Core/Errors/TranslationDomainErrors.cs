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

        /// <summary>
        /// The file held candidate rows but the parser rejected every one of them. Carries the first
        /// rejection, because this is a failure path — the CLI prints the warning list only on a
        /// successful patch, so an error that just said "no translations" would be the exact silence
        /// ADR-0042 set out to remove.
        /// </summary>
        public static Error NoTranslationsEveryLineRejected(int rejectedLineCount, string firstWarning) =>
            Error.Validation(
                "Translation.NoTranslations",
                $"No translations to apply: all {rejectedLineCount} candidate lines were rejected. First: {firstWarning}");
    }

    public static class Export
    {
        public static Error CannotCreateOutputFile(string path, string message) =>
            IoError("Export", "CannotCreateOutput", $"'{path}': {message}");
    }
}
