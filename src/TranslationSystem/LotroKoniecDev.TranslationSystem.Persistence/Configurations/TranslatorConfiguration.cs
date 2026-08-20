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

        // The Auth user id is the key that makes creating a profile on first use safe to repeat
        // (ADR-0004). The unique index keeps it at one row per identity even when two requests create
        // the profile at the same time.
        builder.Property(translator => translator.IdentityId);
        builder.HasIndex(translator => translator.IdentityId)
            .IsUnique();

        // Plain value objects that need no index, so ComplexProperty is the right mapping. Email is
        // optional, so its complex property is nullable.
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
