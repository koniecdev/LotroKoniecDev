using LotroKoniecDev.TranslationSystem.Projections;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LotroKoniecDev.TranslationSystem.Persistence.Configurations;

internal sealed class PrecomputedTranslationFileConfiguration : IEntityTypeConfiguration<PrecomputedTranslationFile>
{
    public void Configure(EntityTypeBuilder<PrecomputedTranslationFile> builder)
    {
        // Physical table name retained from the original aggregate (ADR-0003): the rename is a
        // code-model change only, so no migration is needed.
        builder.ToTable("TranslationArtifacts");

        builder.Property(precomputedTranslationFile => precomputedTranslationFile.Id)
            .ValueGeneratedNever();

        // Get-only property — EF Core convention skips it without an explicit mapping.
        builder.Property(precomputedTranslationFile => precomputedTranslationFile.Language)
            .HasMaxLength(PrecomputedTranslationFile.LanguageMaxLength);

        builder.HasIndex(precomputedTranslationFile => precomputedTranslationFile.Language)
            .IsUnique();

        builder.Property(precomputedTranslationFile => precomputedTranslationFile.ContentHash)
            .HasMaxLength(PrecomputedTranslationFile.ContentHashLength);
    }
}
