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

        // Id, Type, Payload and OccurredOn are get-only, so EF Core maps each one only through its own
        // Property() call. Delete a call that looks empty and the next migration drops the column.
        builder.Property(message => message.Id)
            .ValueGeneratedNever();

        builder.Property(message => message.Type)
            .HasMaxLength(OutboxMessage.TypeMaxLength);

        // text, not jsonb: the row must keep the exact bytes that went on the wire (until erasure,
        // ADR-0065), and jsonb reorders keys and drops whitespace and duplicates. Its validation would
        // only catch errors System.Text.Json cannot make.
        builder.Property(message => message.Payload);

        builder.Property(message => message.OccurredOn);

        builder.Property(message => message.LastError)
            .HasMaxLength(OutboxMessage.LastErrorMaxLength);


        builder.HasIndex(message => message.OccurredOn)
            .HasDatabaseName("IX_OutboxMessages_Unprocessed")
            .HasFilter($"\"{nameof(OutboxMessage.ProcessedOn)}\" IS NULL");
    }
}
