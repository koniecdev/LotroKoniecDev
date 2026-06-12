using LotroKoniecDev.TranslationSystem.ReadModels.Aggregates.GameVersionAggregate;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LotroKoniecDev.TranslationSystem.ReadModels.EntityFramework.Aggregates.GameVersionAggregate;

public sealed class GameVersionReadModelConfiguration : IEntityTypeConfiguration<GameVersionReadModel>
{
    public void Configure(EntityTypeBuilder<GameVersionReadModel> builder)
    {
        builder.ToTable("GameVersions");

        builder.Property(gameVersionReadModel => gameVersionReadModel.Id)
            .ValueGeneratedNever();

        builder.Property(gameVersionReadModel => gameVersionReadModel.Status)
            .HasConversion<string>();

        // CreatedAt is computed from DetectedAt on the read model — there is no such column.
        builder.Ignore(gameVersionReadModel => gameVersionReadModel.CreatedAt);
    }
}
