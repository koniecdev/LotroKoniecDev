using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Persistence.Consts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LotroKoniecDev.TranslationSystem.Persistence.Configurations;

internal sealed class GameVersionConfiguration : IEntityTypeConfiguration<GameVersion>
{
    public void Configure(EntityTypeBuilder<GameVersion> builder)
    {
        builder.ToTable("GameVersions");

        builder.Property(gameVersion => gameVersion.Id)
            .ValueGeneratedNever();

        builder.Property(gameVersion => gameVersion.Version)
            .HasMaxLength(GameVersion.VersionMaxLength);

        builder.HasIndex(gameVersion => gameVersion.Version)
            .IsUnique();

        // Get-only property — EF Core convention skips it without an explicit mapping.
        builder.Property(gameVersion => gameVersion.DetectedAt);

        builder.Property(gameVersion => gameVersion.Status)
            .HasConversion<string>()
            .HasMaxLength(EnumConsts.MaxLength);
    }
}
