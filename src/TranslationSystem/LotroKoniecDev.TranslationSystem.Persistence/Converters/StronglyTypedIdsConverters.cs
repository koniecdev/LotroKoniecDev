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
            .HaveConversion<GameVersionIdConverter>();

        configurationBuilder
            .Properties<TranslationId>()
            .HaveConversion<TranslationIdConverter>();

        configurationBuilder
            .Properties<PrecomputedTranslationFileId>()
            .HaveConversion<PrecomputedTranslationFileIdConverter>();

        configurationBuilder
            .Properties<TranslatorId>()
            .HaveConversion<TranslatorIdConverter>();

        // IdentityId is the AuthSystem user id (cross-context reference) carried by the Translator as
        // the lazy-provisioning key (ADR-0004); it lives in the SharedKernel, not the TMS Primitives,
        // but persists the same way.
        configurationBuilder
            .Properties<IdentityId>()
            .HaveConversion<IdentityIdConverter>();

        return configurationBuilder;
    }

    private sealed class UtcDateTimeOffsetConverter() : ValueConverter<DateTimeOffset, DateTimeOffset>(
        dto => dto.ToUniversalTime(),
        dto => dto.ToUniversalTime());

    private sealed class GameVersionIdConverter() : ValueConverter<GameVersionId, Guid>(
        id => id.Value,
        value => new GameVersionId(value));

    private sealed class TranslationIdConverter() : ValueConverter<TranslationId, Guid>(
        id => id.Value,
        value => new TranslationId(value));

    private sealed class PrecomputedTranslationFileIdConverter() : ValueConverter<PrecomputedTranslationFileId, Guid>(
        id => id.Value,
        value => new PrecomputedTranslationFileId(value));

    private sealed class TranslatorIdConverter() : ValueConverter<TranslatorId, Guid>(
        id => id.Value,
        value => new TranslatorId(value));

    private sealed class IdentityIdConverter() : ValueConverter<IdentityId, Guid>(
        id => id.Value,
        value => new IdentityId(value));
}
