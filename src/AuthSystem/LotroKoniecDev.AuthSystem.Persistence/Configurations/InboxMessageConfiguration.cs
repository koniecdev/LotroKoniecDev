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

        // Both properties are get-only, so EF Core maps each one only through its own Property() call.
        builder.Property(message => message.MessageId)
            .ValueGeneratedNever();

        builder.Property(message => message.ProcessedOn);
    }
}
