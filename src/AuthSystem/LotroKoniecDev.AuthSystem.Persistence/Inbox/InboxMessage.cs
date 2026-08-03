using LotroKoniecDev.SharedKernel.Guards;

namespace LotroKoniecDev.AuthSystem.Persistence.Inbox;

/// <summary>
/// One row per fully processed broker delivery, keyed by the broker message id — which equals the
/// publishing <see cref="Outbox.OutboxMessage"/>'s Id, so the full context of any inbox row is one
/// join away. The consumer checks this table before doing any work and records into it after
/// success, so a redelivered or re-published message is acknowledged without a second side effect
/// (ADR-0037).
/// </summary>
/// <remarks>
/// Deliberately carries no Type, no Payload and no attempt counters: the outbox row with the same
/// id holds the former two, and retry bookkeeping is broker-owned (ADR-0036). One hard constraint
/// from ADR-0037: this table serves the single e-mail consumer — a second consumer of the same
/// message (a fanout binding) must NOT share it without adding a consumer discriminator, or the
/// two would silently skip each other's work.
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
