using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LotroKoniecDev.TranslationSystem.Persistence.Configurations;

internal sealed class TranslatorConfiguration : IEntityTypeConfiguration<Translator>
{
    public void Configure(EntityTypeBuilder<Translator> builder)
    {
        builder.ToTable("Translators");

        builder.Property(translator => translator.Id)
            .ValueGeneratedNever();

        // One row per identity even when two requests create the profile at once (ADR-0004).
        builder.Property(translator => translator.IdentityId);
        builder.HasIndex(translator => translator.IdentityId)
            .IsUnique();

        builder.ComplexProperty(translator => translator.DisplayName, complexBuilder =>
        {
            complexBuilder.Property(displayName => displayName.Value)
                .HasColumnName(nameof(Translator.DisplayName))
                .HasMaxLength(DisplayName.MaxLength);
        });

        builder.ComplexProperty(translator => translator.Email, complexBuilder =>
        {
            complexBuilder.IsRequired(false);
            complexBuilder.Property(email => email.Value)
                .HasColumnName(nameof(Translator.Email))
                .HasMaxLength(Email.MaxLength);
        });

        // A get-only property. EF Core skips it unless it is mapped here.
        builder.Property(translator => translator.ProvisionedAt);
    }
}
