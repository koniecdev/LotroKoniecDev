using LotroKoniecDev.Domain.Core.BuildingBlocks;

namespace LotroKoniecDev.Domain.Core.Errors;

public static partial class DomainErrors
{
    public static class TranslationFileSync
    {
        public const string IntegrityCheckFailedCode = "TranslationFileSync.IntegrityCheckFailed";

        public const string ResponseTooLargeCode = "TranslationFileSync.ResponseTooLarge";

        public static Error NetworkError(string message) =>
            IoError("TranslationFileSync", "NetworkError", message);

        public static Error ResponseTooLarge(long maxResponseBytes) =>
            Error.IoError(ResponseTooLargeCode,
                $"The response body exceeds the maximum allowed size of {maxResponseBytes} bytes.");

        public static Error CacheWriteError(string path, string message) =>
            IoError("TranslationFileSync", "CacheWriteError", $"'{path}': {message}");

        public static Error IntegrityCheckFailed(string message) =>
            Error.IoError(IntegrityCheckFailedCode, $"The downloaded translation file failed the integrity check: {message}");
    }
}
