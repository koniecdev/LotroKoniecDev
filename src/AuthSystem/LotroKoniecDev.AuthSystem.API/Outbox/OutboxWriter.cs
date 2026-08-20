using System.Text.Json;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.AuthSystem.Persistence.Outbox;

namespace LotroKoniecDev.AuthSystem.API.Outbox;

/// <summary>
/// The only way a feature slice writes an e-mail message to the outbox. It serializes the payload,
/// sets the row's <c>Type</c> to the contract's type name, so no writer can mistype the string that
/// both the registry and the routing table look up, and it carries the wake-up call after the commit.
/// Putting the whole ADR-0035 §2 pattern in one injected component means no future writer can rebuild
/// half of it. ADR-0038 decision 6 keeps that wake-up an explicit call and not an interceptor.
/// </summary>
/// <remarks>
/// <see cref="Enqueue{TMessage}"/> only adds the row to the caller's unit of work. The caller commits
/// it together with its own change, which is the whole point of the outbox pattern.
/// After that commit, and only after it, the caller calls <see cref="NotifyEnqueuedCommitted"/> once.
/// The relay reads committed rows, so a signal sent inside the transaction could arrive while there is
/// still nothing to see.
/// </remarks>
internal sealed class OutboxWriter
{
    private readonly AuthDbContext _db;
    private readonly OutboxSignal _outboxSignal;
    private readonly TimeProvider _timeProvider;

    public OutboxWriter(AuthDbContext db, OutboxSignal outboxSignal, TimeProvider timeProvider)
    {
        _db = db;
        _outboxSignal = outboxSignal;
        _timeProvider = timeProvider;
    }

    public void Enqueue<TMessage>(TMessage message) where TMessage : class
    {
        string type = typeof(TMessage).Name;

        // A guard against a programmer error. A contract with no routing entry would commit fine and
        // then block the relay: OutboxMessageRouting fails that row loudly, but only later. Failing
        // the writer's own request shows the missing entry as soon as it is written.
        // It uses its own exception type, so a writer's catch block does not swallow it.
        if (!OutboxMessageRouting.TryGetRoutingKey(type, out _))
        {
            throw new UnroutableOutboxMessageTypeException(type);
        }

        OutboxMessage outboxMessage = OutboxMessage.Create(
            type: type,
            payload: JsonSerializer.Serialize(message),
            occurredOn: _timeProvider.GetUtcNow());

        _db.OutboxMessages.Add(outboxMessage);
    }

    public void NotifyEnqueuedCommitted()
    {
        _outboxSignal.Notify();
    }
}
