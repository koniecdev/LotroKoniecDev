using LotroKoniecDev.TranslationSystem.ReadModels.Projections;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LotroKoniecDev.TranslationSystem.ReadModels.EntityFramework.Projections;

public sealed class PrecomputedTranslationFileReadModelConfiguration : IEntityTypeConfiguration<PrecomputedTranslationFileReadModel>
{
    public void Configure(EntityTypeBuilder<PrecomputedTranslationFileReadModel> builder)
    {
        // Physical table name retained from the original aggregate (ADR-0007): the rename is a
        // code-model change only, so no migration is needed.
        builder.ToTable("TranslationArtifacts");

        builder.Property(precomputedTranslationFile => precomputedTranslationFile.Id)
            .ValueGeneratedNever();

        // CreatedAt is computed from GeneratedAt on the read model — there is no such column.
        builder.Ignore(precomputedTranslationFile => precomputedTranslationFile.CreatedAt);
    }
}
