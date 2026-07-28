using LotroKoniecDev.AuthSystem.Persistence.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LotroKoniecDev.AuthSystem.Persistence.Configurations;

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("OutboxMessages");

        builder.HasKey(message => message.Id);

        builder.Property(message => message.Id)
            .ValueGeneratedNever();

        builder.Property(message => message.Type)
            .HasMaxLength(OutboxMessage.TypeMaxLength);

        builder.Property(message => message.Payload);

        builder.Property(message => message.OccurredOn);

        builder.Property(message => message.LastError)
            .HasMaxLength(OutboxMessage.LastErrorMaxLength);
        
        builder.HasIndex(message => message.OccurredOn)
            .HasDatabaseName("IX_OutboxMessages_Unprocessed")
            .HasFilter($"\"{nameof(OutboxMessage.ProcessedOn)}\" IS NULL");
    }
}
