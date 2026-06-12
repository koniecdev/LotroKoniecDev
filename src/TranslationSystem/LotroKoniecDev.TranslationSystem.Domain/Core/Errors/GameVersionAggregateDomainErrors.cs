using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;

namespace LotroKoniecDev.TranslationSystem.Domain.Core.Errors;

public static partial class DomainErrors
{
    public static class GameVersionEntity
    {
        public static Error NotFound(GameVersionId id)
            => HasNotBeenFound(nameof(GameVersionEntity), id.Value);

        public static Error VersionRequired
            => Required(nameof(GameVersionEntity), "Version");

        public static Error VersionTooLong(int maxLength)
            => TooManyCharacters(nameof(GameVersionEntity), "Version", maxLength);

        public static Error VersionAlreadyRegistered(string version)
            => AlreadyHasBeenTaken(nameof(GameVersionEntity), "Version", version);

        public static Error SupersededCannotBeProcessed(GameVersionId id)
            => InvalidOperation(
                nameof(GameVersionEntity),
                $"Game version with ID '{id.Value}' is superseded and can never be processed.",
                "SupersededCannotBeProcessed");

        public static Error ProcessedCannotBeSuperseded(GameVersionId id)
            => InvalidOperation(
                nameof(GameVersionEntity),
                $"Game version with ID '{id.Value}' is already processed and cannot be superseded.",
                "ProcessedCannotBeSuperseded");
    }
}
