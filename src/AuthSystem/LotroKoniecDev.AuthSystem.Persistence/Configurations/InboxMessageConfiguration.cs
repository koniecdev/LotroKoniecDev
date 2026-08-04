using LotroKoniecDev.AuthSystem.Persistence.Inbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LotroKoniecDev.AuthSystem.Persistence.Configurations;

internal sealed class InboxMessageConfiguration : IEntityTypeConfiguration<InboxMessage>
{
    public void Configure(EntityTypeBuilder<InboxMessage> builder)
    {
        builder.ToTable("InboxMessages");

        builder.HasKey(message => message.MessageId);

        // Both properties are get-only, and EF Core's convention only discovers properties that
        // have BOTH a getter and a setter — each needs an explicit Property() call to exist in
        // the model at all (same gotcha as OutboxMessageConfiguration).
        builder.Property(message => message.MessageId)
            .ValueGeneratedNever();

        builder.Property(message => message.ProcessedOn);
    }
}
