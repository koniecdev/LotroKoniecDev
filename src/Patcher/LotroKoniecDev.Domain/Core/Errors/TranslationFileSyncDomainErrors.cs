using LotroKoniecDev.Domain.Core.BuildingBlocks;

namespace LotroKoniecDev.Domain.Core.Errors;

public static partial class DomainErrors
{
    public static class TranslationFileSync
    {
        public const string IntegrityCheckFailedCode = "TranslationFileSync.IntegrityCheckFailed";

        public const string ResponseTooLargeCode = "TranslationFileSync.ResponseTooLarge";

        public const string EndpointDiscoveryUnavailableCode = "TranslationFileSync.EndpointDiscoveryUnavailable";

        public const string EndpointNotAdvertisedCode = "TranslationFileSync.EndpointNotAdvertised";

        public const string EndpointRejectedCode = "TranslationFileSync.EndpointRejected";

        public static Error NetworkError(string message) =>
            IoError("TranslationFileSync", "NetworkError", message);

        public static Error ResponseTooLarge(long maxResponseBytes) =>
            Error.IoError(ResponseTooLargeCode,
                $"The response body exceeds the maximum allowed size of {maxResponseBytes} bytes.");

        public static Error CacheWriteError(string path, string message) =>
            IoError("TranslationFileSync", "CacheWriteError", $"'{path}': {message}");

        public static Error IntegrityCheckFailed(string message) =>
            Error.IoError(IntegrityCheckFailedCode, $"The downloaded translation file failed the integrity check: {message}");

        /// <summary>We could not read the service document and no usable endpoint was cached (#611).</summary>
        public static Error EndpointDiscoveryUnavailable(string message) =>
            Error.IoError(EndpointDiscoveryUnavailableCode,
                $"The translation-file endpoint could not be resolved from the server: {message}");

        /// <summary>The server answered, but its service document does not offer this action to this caller.</summary>
        public static Error EndpointNotAdvertised(string rel) =>
            Error.IoError(EndpointNotAdvertisedCode,
                $"The server's service document does not advertise a '{rel}' link, so the translation file is not on offer.");

        public static Error EndpointRejected(string href, string reason) =>
            Error.IoError(EndpointRejectedCode,
                $"The translation-file endpoint '{href}' was rejected: {reason}");
    }
}
