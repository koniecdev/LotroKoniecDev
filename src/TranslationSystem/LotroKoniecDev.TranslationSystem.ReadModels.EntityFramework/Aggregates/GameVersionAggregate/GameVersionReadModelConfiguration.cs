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

        // The read model computes CreatedAt from DetectedAt. There is no such column in the table.
        builder.Ignore(gameVersionReadModel => gameVersionReadModel.CreatedAt);
    }
}
