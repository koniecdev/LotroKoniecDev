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

        // The cross-context Auth user id is the lazy-provisioning idempotency key (ADR-0004): a
        // unique index guarantees one row per identity even under a concurrent first-write race.
        builder.Property(translator => translator.IdentityId);
        builder.HasIndex(translator => translator.IdentityId)
            .IsUnique();

        // Pure value types with no index needed — ComplexProperty (the semantically correct VO
        // mapping); Email is optional so its complex property is nullable.
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

        // Get-only property — EF Core convention skips it without an explicit mapping.
        builder.Property(translator => translator.ProvisionedAt);
    }
}
