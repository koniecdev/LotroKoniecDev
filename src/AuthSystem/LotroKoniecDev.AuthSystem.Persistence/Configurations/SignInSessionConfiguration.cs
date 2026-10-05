using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.Sessions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LotroKoniecDev.AuthSystem.Persistence.Configurations;

internal sealed class SignInSessionConfiguration : IEntityTypeConfiguration<SignInSession>
{
    public void Configure(EntityTypeBuilder<SignInSession> builder)
    {
        builder.ToTable("SignInSessions");

        builder.HasKey(session => session.Id);

        // Every property is get-only, and EF Core only finds properties that have a getter and a setter.
        // Each one needs its own Property() call to exist in the model at all, the same trap as in
        // OutboxMessageConfiguration.
        builder.Property(session => session.Id)
            .ValueGeneratedNever();

        builder.Property(session => session.UserId);

        builder.Property(session => session.ProtectedTicket);

        builder.Property(session => session.ExpiresAt);

        // The foreign key also gives the index the account erasure deletes by.
        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(session => session.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
