using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Projections;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace LotroKoniecDev.TranslationSystem.Persistence.Converters;

public static class StronglyTypedIdsConverters
{
    public static ModelConfigurationBuilder RegisterAllStronglyTypedIdConverters(
        this ModelConfigurationBuilder configurationBuilder)
    {
        // Npgsql's `timestamp with time zone` only accepts DateTimeOffset values with UTC offset.
        // Normalize on the way in so callers don't have to remember to call ToUniversalTime().
        configurationBuilder
            .Properties<DateTimeOffset>()
            .HaveConversion<UtcDateTimeOffsetConverter>();

        configurationBuilder
            .Properties<GameVersionId>()
            .HaveConversion<StronglyTypedIdValueConverter<GameVersionId>>();

        configurationBuilder
            .Properties<TranslationId>()
            .HaveConversion<StronglyTypedIdValueConverter<TranslationId>>();

        configurationBuilder
            .Properties<PrecomputedTranslationFileId>()
            .HaveConversion<StronglyTypedIdValueConverter<PrecomputedTranslationFileId>>();

        configurationBuilder
            .Properties<TranslatorId>()
            .HaveConversion<StronglyTypedIdValueConverter<TranslatorId>>();

        // IdentityId is the AuthSystem user id (cross-context reference) carried by the Translator as
        // the lazy-provisioning key (ADR-0004); it lives in the SharedKernel, not the TMS Primitives,
        // but persists the same way.
        configurationBuilder
            .Properties<IdentityId>()
            .HaveConversion<StronglyTypedIdValueConverter<IdentityId>>();

        return configurationBuilder;
    }

    private sealed class UtcDateTimeOffsetConverter() : ValueConverter<DateTimeOffset, DateTimeOffset>(
        dto => dto.ToUniversalTime(),
        dto => dto.ToUniversalTime());
}
