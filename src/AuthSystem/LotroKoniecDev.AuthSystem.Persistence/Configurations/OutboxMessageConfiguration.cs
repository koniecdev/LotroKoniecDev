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

        // Id, Type, Payload and OccurredOn are get-only, and EF Core only finds properties that have
        // a getter and a setter. Each one needs its own Property() call to exist in the model at all.
        // Delete a call below that looks empty and the column quietly disappears, which the next
        // migration writes out as DropColumn. ProcessedOn and Attempts have a private setter, so EF
        // maps them on its own.
        builder.Property(message => message.Id)
            .ValueGeneratedNever();

        builder.Property(message => message.Type)
            .HasMaxLength(OutboxMessage.TypeMaxLength);

        // text on purpose, not jsonb. An outbox row must record exactly what went on the wire, and
        // jsonb stores a parsed document: it reorders keys, drops duplicates and whitespace, and
        // rewrites numbers, so reading it back would not give the same bytes. Any future hash or
        // signature over the payload depends on that.
        // The checking jsonb would add guards against a kind of error System.Text.Json cannot make
        // anyway, and it would move that error inside the registration transaction.
        builder.Property(message => message.Payload);

        builder.Property(message => message.OccurredOn);

        builder.Property(message => message.LastError)
            .HasMaxLength(OutboxMessage.LastErrorMaxLength);


        builder.HasIndex(message => message.OccurredOn)
            .HasDatabaseName("IX_OutboxMessages_Unprocessed")
            .HasFilter($"\"{nameof(OutboxMessage.ProcessedOn)}\" IS NULL");
    }
}
