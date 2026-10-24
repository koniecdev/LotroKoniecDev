using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.SharedKernel.Constants;

namespace LotroKoniecDev.AuthSystem.Persistence.Configurations;

internal sealed class ApplicationUserConfiguration : IEntityTypeConfiguration<ApplicationUser>
{
    public void Configure(EntityTypeBuilder<ApplicationUser> builder)
    {
        builder.ToTable("Users");
        builder.HasIndex(u => u.UserName).IsUnique();
        builder.HasIndex(u => u.Email).IsUnique();

        // Identity maps this index as non-unique and checks RequireUniqueEmail in the app only. Two
        // addresses that differ only in case would make FindByEmailAsync throw on login and lock both
        // accounts out (ADR-0022). The name has to stay "EmailIndex", or Identity adds a second index.
        builder.HasIndex(u => u.NormalizedEmail)
            .IsUnique()
            .HasDatabaseName("EmailIndex");

        builder.Property(u => u.DataProcessingConsentGiven)
            .HasDefaultValue(false);

        builder.Property(u => u.DataProcessingConsentDate);

        builder.Property(u => u.PrivacyPolicyAccepted)
            .HasDefaultValue(false);

        builder.Property(u => u.PrivacyPolicyAcceptedDate);

        builder.Property(u => u.TermsOfServiceAccepted)
            .HasDefaultValue(false);

        builder.Property(u => u.TermsOfServiceAcceptedDate);

        builder.Property(u => u.DeletionScheduledAt);

        builder.Property(u => u.EmailChangeRevertStamp);

        builder.Property(u => u.EmailChangeRevertTo)
            .HasMaxLength(EmailConstants.MaxLength);

        builder.Property(u => u.NormalizedEmailChangeRevertTo)
            .HasMaxLength(EmailConstants.MaxLength);

        builder.Property(u => u.EmailChangeRevertArmedAt);

        // Partial, because registration checks the reservation on every attempt and only the few rows
        // with an armed undo can match (#684). Not unique: an expired arming leaves its value behind,
        // and a later account may arm the same address.
        builder.HasIndex(u => u.NormalizedEmailChangeRevertTo)
            .HasFilter($"\"{nameof(ApplicationUser.NormalizedEmailChangeRevertTo)}\" IS NOT NULL");

        // A partial index. The deletion job only looks at the few rows that have a date set.
        builder.HasIndex(u => u.DeletionScheduledAt)
            .HasFilter($"\"{nameof(ApplicationUser.DeletionScheduledAt)}\" IS NOT NULL");
    }
}
