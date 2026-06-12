using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationRoles.Entities;

namespace LotroKoniecDev.AuthSystem.Persistence.Configurations;

internal sealed class ApplicationRoleConfiguration : IEntityTypeConfiguration<ApplicationRole>
{
    public void Configure(EntityTypeBuilder<ApplicationRole> builder)
    {
        builder.ToTable("Roles");
        builder.HasIndex(r => r.Name).IsUnique();
    }
}
