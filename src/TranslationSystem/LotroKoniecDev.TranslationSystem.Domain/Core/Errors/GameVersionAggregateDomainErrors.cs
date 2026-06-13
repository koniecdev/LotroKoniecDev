using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;

namespace LotroKoniecDev.TranslationSystem.Domain.Core.Errors;

public static partial class DomainErrors
{
    public static class GameVersionEntity
    {
        public static Error NotFound(GameVersionId id)
            => HasNotBeenFound(nameof(GameVersionEntity), id.Value);

        public static Error VersionAlreadyRegistered(string version)
            => AlreadyHasBeenTaken(nameof(GameVersionEntity), nameof(GameVersion.LotroNotationVersion), version);

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

        public static class VersionProperty
        {
            public static Error NullOrEmpty
                => Required(nameof(GameVersionEntity), nameof(GameVersion.LotroNotationVersion));

            public static Error LongerThanAllowed
                => TooManyCharacters(nameof(GameVersionEntity), nameof(GameVersion.LotroNotationVersion), LotroNotationVersion.VersionMaxLength);
        }
    }
}
