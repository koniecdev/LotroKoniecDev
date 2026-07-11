using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;

namespace LotroKoniecDev.AuthSystem.Persistence.Configurations;

internal sealed class ApplicationUserConfiguration : IEntityTypeConfiguration<ApplicationUser>
{
    public void Configure(EntityTypeBuilder<ApplicationUser> builder)
    {
        builder.ToTable("Users");
        builder.HasIndex(u => u.UserName).IsUnique();
        builder.HasIndex(u => u.Email).IsUnique();

        // Identity's base mapping creates this index non-unique. RequireUniqueEmail is app-level
        // only, and the login path resolves FindByEmailAsync — a case-variant duplicate pair
        // (Foo@x.com / foo@x.com) would throw there and lock both accounts out (ADR-0022).
        // The database name must stay "EmailIndex", or the base mapping yields a second index.
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

        // Partial index: the deletion finalizer polls only the handful of rows with a schedule set.
        builder.HasIndex(u => u.DeletionScheduledAt)
            .HasFilter($"\"{nameof(ApplicationUser.DeletionScheduledAt)}\" IS NOT NULL");
    }
}
