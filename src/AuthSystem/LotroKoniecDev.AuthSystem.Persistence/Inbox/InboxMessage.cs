using LotroKoniecDev.SharedKernel.Guards;

namespace LotroKoniecDev.AuthSystem.Persistence.Inbox;

/// <summary>
/// One row for each broker delivery that was fully processed, keyed by the broker message id. That id
/// is the same as the Id of the <see cref="Outbox.OutboxMessage"/> that published it, so the full
/// context of any row here is one join away.
/// The consumer checks this table before it does any work and writes to it after success, so a message
/// delivered or published twice is acknowledged without doing the work again (ADR-0037).
/// </summary>
/// <remarks>
/// It carries no Type, no Payload and no attempt count on purpose: the outbox row with the same id
/// holds the first two, and the broker owns the retry counting (ADR-0036).
/// One firm rule from ADR-0037: this table serves the single e-mail consumer. A second consumer of the
/// same message, through a fanout binding, must not share it without a column saying which consumer a
/// row belongs to, or the two would quietly skip each other's work.
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
