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

        // Identity's own mapping makes this index non-unique. RequireUniqueEmail is checked in the
        // app only, and the login path calls FindByEmailAsync. So a pair that differs only in case,
        // such as Foo@x.com and foo@x.com, would make that call throw and lock both accounts out
        // (ADR-0022). The database name has to stay "EmailIndex", or Identity adds a second index.
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

        // A partial index. The deletion job only looks at the few rows that have a date set.
        builder.HasIndex(u => u.DeletionScheduledAt)
            .HasFilter($"\"{nameof(ApplicationUser.DeletionScheduledAt)}\" IS NOT NULL");
    }
}
