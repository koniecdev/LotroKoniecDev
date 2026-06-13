using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Persistence.Consts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LotroKoniecDev.TranslationSystem.Persistence.Configurations;

internal sealed class TranslationConfiguration : IEntityTypeConfiguration<Translation>
{
    public void Configure(EntityTypeBuilder<Translation> builder)
    {
        builder.ToTable("Translations");

        builder.Property(translation => translation.Id)
            .ValueGeneratedNever();

        // (FileId, GossipId) is the natural identity and needs a unique index, so the VO is mapped
        // with OwnsOne (ComplexProperty cannot be indexed in EF Core 10).
        builder.OwnsOne(translation => translation.FragmentKey, ownedBuilder =>
        {
            ownedBuilder.Property(key => key.FileId)
                .HasColumnName(nameof(FragmentKey.FileId));

            ownedBuilder.Property(key => key.GossipId)
                .HasColumnName(nameof(FragmentKey.GossipId));

            ownedBuilder.HasIndex(key => new { key.FileId, key.GossipId })
                .IsUnique();
        });

        // The English source is a pure value compared as one unit by the diff — ComplexProperty,
        // no index needed.
        builder.ComplexProperty(translation => translation.Source, complexBuilder =>
        {
            complexBuilder.Property(source => source.Text)
                .HasColumnName(nameof(Translation.Source) + nameof(TranslationSource.Text));

            complexBuilder.Property(source => source.ArgsOrder)
                .HasColumnName(nameof(TranslationSource.ArgsOrder));

            complexBuilder.Property(source => source.ArgsId)
                .HasColumnName(nameof(TranslationSource.ArgsId));
        });

        builder.Property(translation => translation.Status)
            .HasConversion<string>()
            .HasMaxLength(EnumConsts.MaxLength);

        // Get-only property — EF Core convention skips it without an explicit mapping.
        builder.Property(translation => translation.CreatedAt);
    }
}
