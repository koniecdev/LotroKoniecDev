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

        // Joins to the submitter and the approver so the display name can be shown (ADR-0004). Both
        // are optional: a new row has no submitter yet, and no approver until someone approves it.
        builder.HasOne(translationReadModel => translationReadModel.SubmittedBy)
            .WithMany()
            .HasForeignKey(translationReadModel => translationReadModel.SubmittedById);

        builder.HasOne(translationReadModel => translationReadModel.ApprovedBy)
            .WithMany()
            .HasForeignKey(translationReadModel => translationReadModel.ApprovedById);
    }
}
