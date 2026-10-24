using LotroKoniecDev.TranslationSystem.Projections;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LotroKoniecDev.TranslationSystem.Persistence.Configurations;

internal sealed class PrecomputedTranslationFileConfiguration : IEntityTypeConfiguration<PrecomputedTranslationFile>
{
    public void Configure(EntityTypeBuilder<PrecomputedTranslationFile> builder)
    {
        // The table keeps its old name; only the class was renamed (ADR-0007).
        builder.ToTable("TranslationArtifacts");

        builder.Property(precomputedTranslationFile => precomputedTranslationFile.Id)
            .ValueGeneratedNever();

        // Get-only properties. EF Core skips them unless they are mapped here.
        builder.Property(precomputedTranslationFile => precomputedTranslationFile.Language)
            .HasMaxLength(PrecomputedTranslationFile.LanguageMaxLength);

        builder.HasIndex(precomputedTranslationFile => precomputedTranslationFile.Language)
            .IsUnique();

        builder.Property(precomputedTranslationFile => precomputedTranslationFile.Content);

        builder.Property(precomputedTranslationFile => precomputedTranslationFile.ContentHash)
            .HasMaxLength(PrecomputedTranslationFile.ContentHashLength);

        builder.Property(precomputedTranslationFile => precomputedTranslationFile.GeneratedAt);
    }
}
