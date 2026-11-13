using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Persistence.SaveBudget;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LotroKoniecDev.TranslationSystem.Persistence.Configurations;

internal sealed class TranslationSaveConfiguration : IEntityTypeConfiguration<TranslationSave>
{
    public const string TableName = "TranslationSaves";

    public void Configure(EntityTypeBuilder<TranslationSave> builder)
    {
        builder.ToTable(TableName);

        builder.Property(translationSave => translationSave.Id)
            .ValueGeneratedNever();

        builder.Property(translationSave => translationSave.TranslatorId)
            .HasColumnName(nameof(TranslationSave.TranslatorId));

        // Cascade, unlike the Restrict keys on Translations: a budget row is not work, and it means
        // nothing without its account.
        builder.HasOne<Translator>()
            .WithMany()
            .HasForeignKey(translationSave => translationSave.TranslatorId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(translationSave => translationSave.SavedAt);

        builder.Property(translationSave => translationSave.ReplacedPolish);

        // Serves the per-account count of the window.
        builder.HasIndex(translationSave => new { translationSave.TranslatorId, translationSave.SavedAt });
    }
}
