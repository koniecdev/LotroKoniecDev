using LotroKoniecDev.Domain.Core.BuildingBlocks;

namespace LotroKoniecDev.Domain.Core.Errors;

public static partial class DomainErrors
{
    public static class TranslationFileSync
    {
        public static Error NetworkError(string message) =>
            IoError("TranslationFileSync", "NetworkError", message);

        public static Error CacheWriteError(string path, string message) =>
            IoError("TranslationFileSync", "CacheWriteError", $"'{path}': {message}");
    }
}
