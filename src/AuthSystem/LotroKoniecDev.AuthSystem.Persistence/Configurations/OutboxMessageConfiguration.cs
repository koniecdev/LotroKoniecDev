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

        // Id, Type, Payload and OccurredOn are get-only, and EF Core's convention only discovers
        // properties that have BOTH a getter and a setter. Every one of them therefore needs an
        // explicit Property() call to exist in the model at all — delete a seemingly empty call
        // below and the column silently vanishes, which the next migration renders as DropColumn.
        // (ProcessedOn and Attempts carry a private setter, so convention does map them.)
        builder.Property(message => message.Id)
            .ValueGeneratedNever();

        builder.Property(message => message.Type)
            .HasMaxLength(OutboxMessage.TypeMaxLength);

        // Deliberately text, not jsonb: an outbox row must be a byte-faithful record of what was
        // put on the wire, and jsonb stores a parsed document — it reorders keys, drops duplicates
        // and whitespace, and normalises numbers, so a read-back would no longer equal the write.
        // That fidelity is what any future payload hash or signature would rest on. The validation
        // jsonb would add guards a class of failure System.Text.Json cannot produce anyway, and it
        // would move that failure inside the registration transaction.
        builder.Property(message => message.Payload);

        builder.Property(message => message.OccurredOn);

        builder.Property(message => message.LastError)
            .HasMaxLength(OutboxMessage.LastErrorMaxLength);


        builder.HasIndex(message => message.OccurredOn)
            .HasDatabaseName("IX_OutboxMessages_Unprocessed")
            .HasFilter($"\"{nameof(OutboxMessage.ProcessedOn)}\" IS NULL");
    }
}
