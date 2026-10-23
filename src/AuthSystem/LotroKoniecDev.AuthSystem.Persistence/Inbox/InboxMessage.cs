using LotroKoniecDev.SharedKernel.Guards;

namespace LotroKoniecDev.AuthSystem.Persistence.Inbox;

/// <summary>
/// One row for each fully processed broker delivery, keyed by the broker message id, which is the Id of
/// the <see cref="Outbox.OutboxMessage"/> that published it. The consumer checks this table before any
/// work and writes to it after success, so a duplicate delivery is acknowledged without doing the work
/// again (ADR-0037).
/// </summary>
/// <remarks>
/// No Type, Payload or attempt count: the outbox row with the same id holds the first two, and the
/// broker counts retries (ADR-0036).
/// This table serves the single e-mail consumer. A second consumer of the same message must not share it
/// without a column naming the consumer, or the two would skip each other's work (ADR-0037).
/// </remarks>
public sealed class InboxMessage
{
    public Guid MessageId { get; }
    public DateTimeOffset ProcessedOn { get; }

    public static InboxMessage Create(Guid messageId, DateTimeOffset processedOn)
    {
        Ensure.NotEmpty(messageId);
        Ensure.NotEmpty(processedOn);
        InboxMessage instance = new(messageId: messageId, processedOn: processedOn);
        return instance;
    }

    private InboxMessage(Guid messageId, DateTimeOffset processedOn)
    {
        MessageId = messageId;
        ProcessedOn = processedOn;
    }

    private InboxMessage()
    {
    }
}
