using LotroKoniecDev.TranslationSystem.ReadModels.Projections;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LotroKoniecDev.TranslationSystem.ReadModels.EntityFramework.Projections;

public sealed class PrecomputedTranslationFileReadModelConfiguration : IEntityTypeConfiguration<PrecomputedTranslationFileReadModel>
{
    public void Configure(EntityTypeBuilder<PrecomputedTranslationFileReadModel> builder)
    {
        // The table keeps its old name; only the class was renamed (ADR-0007).
        builder.ToTable("TranslationArtifacts");

        builder.Property(precomputedTranslationFile => precomputedTranslationFile.Id)
            .ValueGeneratedNever();

        // The read model computes CreatedAt from GeneratedAt. There is no such column in the table.
        builder.Ignore(precomputedTranslationFile => precomputedTranslationFile.CreatedAt);
    }
}
