using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Enums;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;

namespace LotroKoniecDev.TranslationSystem.Domain.Core.Errors;

public static partial class DomainErrors
{
    public static class TranslationEntity
    {
        public static Error NotFound(TranslationId id)
            => HasNotBeenFound(nameof(TranslationEntity), id.Value);

        public static Error CannotApproveWithoutTranslation
            => InvalidOperation(nameof(TranslationEntity),
                "A translation cannot be approved without Polish content.",
                "CannotApproveWithoutTranslation");

        public static Error CannotApproveRemoved
            => InvalidOperation(nameof(TranslationEntity),
                "A soft-removed translation cannot be approved.",
                "CannotApproveRemoved");

        public static class FragmentKeyProperty
        {
            public static Error InvalidFileId
                => new($"{nameof(TranslationEntity)}.FileId.Invalid",
                    "The file id must be a positive number.",
                    TypeOfError.Validation);

            public static Error InvalidGossipId
                => new($"{nameof(TranslationEntity)}.GossipId.Invalid",
                    "The gossip id must not be negative.",
                    TypeOfError.Validation);
        }

        public static class SourceProperty
        {
            public static Error TextRequired
                => Required(nameof(TranslationEntity), "SourceText");
        }
    }
}
