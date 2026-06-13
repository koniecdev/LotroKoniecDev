using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationArtifactAggregate.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LotroKoniecDev.TranslationSystem.Persistence.Configurations;

internal sealed class TranslationArtifactConfiguration : IEntityTypeConfiguration<TranslationArtifact>
{
    public void Configure(EntityTypeBuilder<TranslationArtifact> builder)
    {
        builder.ToTable("TranslationArtifacts");

        builder.Property(translationArtifact => translationArtifact.Id)
            .ValueGeneratedNever();

        // Get-only property — EF Core convention skips it without an explicit mapping.
        builder.Property(translationArtifact => translationArtifact.Language)
            .HasMaxLength(TranslationArtifact.LanguageMaxLength);

        builder.HasIndex(translationArtifact => translationArtifact.Language)
            .IsUnique();

        builder.Property(translationArtifact => translationArtifact.ContentHash)
            .HasMaxLength(TranslationArtifact.ContentHashLength);
    }
}
