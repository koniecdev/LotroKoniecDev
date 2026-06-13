using LotroKoniecDev.TranslationSystem.ReadModels.Aggregates.TranslationArtifactAggregate;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LotroKoniecDev.TranslationSystem.ReadModels.EntityFramework.Aggregates.TranslationArtifactAggregate;

public sealed class TranslationArtifactReadModelConfiguration : IEntityTypeConfiguration<TranslationArtifactReadModel>
{
    public void Configure(EntityTypeBuilder<TranslationArtifactReadModel> builder)
    {
        builder.ToTable("TranslationArtifacts");

        builder.Property(translationArtifactReadModel => translationArtifactReadModel.Id)
            .ValueGeneratedNever();

        // CreatedAt is computed from GeneratedAt on the read model — there is no such column.
        builder.Ignore(translationArtifactReadModel => translationArtifactReadModel.CreatedAt);
    }
}
