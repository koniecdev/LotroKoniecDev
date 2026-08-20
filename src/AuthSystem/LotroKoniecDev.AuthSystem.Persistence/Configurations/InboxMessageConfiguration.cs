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

        // Both properties are get-only, and EF Core only finds properties that have a getter and a
        // setter. Each one needs its own Property() call to exist in the model at all. The same trap
        // applies in OutboxMessageConfiguration.
        builder.Property(message => message.MessageId)
            .ValueGeneratedNever();

        builder.Property(message => message.ProcessedOn);
    }
}
