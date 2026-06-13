using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.ValueObjects;
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

        builder.OwnsOne(gameVersion => gameVersion.LotroNotationVersion, ownedBuilder =>
        {
            ownedBuilder.Property(v => v.Value)
                .HasColumnName(nameof(GameVersion.LotroNotationVersion))
                .HasMaxLength(LotroNotationVersion.VersionMaxLength);

            ownedBuilder.HasIndex(v => v.Value)
                .IsUnique();
        });

        // Get-only property — EF Core convention skips it without an explicit mapping.
        builder.Property(gameVersion => gameVersion.DetectedAt);

        builder.Property(gameVersion => gameVersion.Status)
            .HasConversion<string>()
            .HasMaxLength(EnumConsts.MaxLength);
    }
}
