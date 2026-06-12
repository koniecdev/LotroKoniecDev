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

        builder.Property(u => u.DataProcessingConsentGiven)
            .HasDefaultValue(false);

        builder.Property(u => u.DataProcessingConsentDate);

        builder.Property(u => u.PrivacyPolicyAccepted)
            .HasDefaultValue(false);

        builder.Property(u => u.PrivacyPolicyAcceptedDate);
    }
}
