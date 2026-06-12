using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
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

        return configurationBuilder;
    }

    private sealed class UtcDateTimeOffsetConverter() : ValueConverter<DateTimeOffset, DateTimeOffset>(
        dto => dto.ToUniversalTime(),
        dto => dto.ToUniversalTime());

    private sealed class GameVersionIdConverter() : ValueConverter<GameVersionId, Guid>(
        id => id.Value,
        value => new GameVersionId(value));
}
