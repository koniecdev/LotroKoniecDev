using LotroKoniecDev.TranslationSystem.ReadModels.Aggregates.TranslationAggregate;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LotroKoniecDev.TranslationSystem.ReadModels.EntityFramework.Aggregates.TranslationAggregate;

public sealed class TranslationReadModelConfiguration : IEntityTypeConfiguration<TranslationReadModel>
{
    public void Configure(EntityTypeBuilder<TranslationReadModel> builder)
    {
        builder.ToTable("Translations");

        builder.Property(translationReadModel => translationReadModel.Id)
            .ValueGeneratedNever();

        builder.Property(translationReadModel => translationReadModel.Status)
            .HasConversion<string>();

        // Submitter / approver join for display-name resolution (ADR-0004); both optional — a row is
        // born untranslated (no submitter) and is approved later (no approver until then).
        builder.HasOne(translationReadModel => translationReadModel.SubmittedBy)
            .WithMany()
            .HasForeignKey(translationReadModel => translationReadModel.SubmittedById);

        builder.HasOne(translationReadModel => translationReadModel.ApprovedBy)
            .WithMany()
            .HasForeignKey(translationReadModel => translationReadModel.ApprovedById);
    }
}
