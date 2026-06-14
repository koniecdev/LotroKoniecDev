using LotroKoniecDev.TranslationSystem.ReadModels.Aggregates.TranslatorAggregate;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LotroKoniecDev.TranslationSystem.ReadModels.EntityFramework.Aggregates.TranslatorAggregate;

public sealed class TranslatorReadModelConfiguration : IEntityTypeConfiguration<TranslatorReadModel>
{
    public void Configure(EntityTypeBuilder<TranslatorReadModel> builder)
    {
        builder.ToTable("Translators");

        builder.Property(translatorReadModel => translatorReadModel.Id)
            .ValueGeneratedNever();
    }
}
