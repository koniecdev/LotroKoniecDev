using System.Text.Json;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.AuthSystem.Persistence.Outbox;

namespace LotroKoniecDev.AuthSystem.API.Outbox;

/// <summary>
/// The only way code writes a message to the outbox: e-mail work, and the account events the TMS reads
/// (ADR-0065). It serializes the payload and sets the row's <c>Type</c> to the contract's type name, so
/// no writer can mistype the string that both the registry and the routing table look up.
/// </summary>
/// <remarks>
/// <see cref="Enqueue{TMessage}"/> only adds the row to the caller's unit of work. The caller commits
/// it together with its own change, which is the whole point of the outbox pattern.
/// The caller does nothing after that commit: <see cref="OutboxWakeUpInterceptor"/> wakes the relay
/// (ADR-0038 §6, amended by #1115).
/// </remarks>
internal sealed class OutboxWriter
{
    private readonly AuthDbContext _db;
    private readonly TimeProvider _timeProvider;

    public OutboxWriter(AuthDbContext db, TimeProvider timeProvider)
    {
        _db = db;
        _timeProvider = timeProvider;
    }

    public void Enqueue<TMessage>(TMessage message) where TMessage : class
    {
        string type = typeof(TMessage).Name;

        // A guard against a programmer error. A contract with no routing entry would commit fine and
        // then block the relay: OutboxMessageRouting fails that row loudly, but only later. Failing
        // the writer's own request shows the missing entry as soon as it is written.
        // It uses its own exception type, so a writer's catch block does not swallow it.
        if (!OutboxMessageRouting.TryGetRoute(type, out _))
        {
            throw new UnroutableOutboxMessageTypeException(type);
        }

        OutboxMessage outboxMessage = OutboxMessage.Create(
            type: type,
            payload: JsonSerializer.Serialize(message),
            occurredOn: _timeProvider.GetUtcNow());

        _db.OutboxMessages.Add(outboxMessage);
    }
}
