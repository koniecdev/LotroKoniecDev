using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;

namespace LotroKoniecDev.TranslationSystem.Domain.Core.Errors;

public static partial class DomainErrors
{
    public static class TranslatorEntity
    {
        public static Error NotFound(TranslatorId id)
            => HasNotBeenFound(nameof(TranslatorEntity), id.Value);

        public static class DisplayNameProperty
        {
            public static Error NullOrEmpty
                => Required(nameof(TranslatorEntity), nameof(Translator.DisplayName));

            public static Error LongerThanAllowed
                => TooManyCharacters(nameof(TranslatorEntity), nameof(Translator.DisplayName), DisplayName.MaxLength);
        }

        public static class EmailProperty
        {
            public static Error LongerThanAllowed
                => TooManyCharacters(nameof(TranslatorEntity), nameof(Translator.Email), Email.MaxLength);

            public static Error InvalidFormat
                => new($"{nameof(TranslatorEntity)}.{nameof(Translator.Email)}.InvalidFormat",
                    $"The {nameof(Translator.Email).ToLowerInvariant()} is not a valid email address.",
                    SharedKernel.Enums.TypeOfError.Validation);
        }
    }
}
