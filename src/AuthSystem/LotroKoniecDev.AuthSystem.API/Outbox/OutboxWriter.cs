using System.Text.Json;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.AuthSystem.Persistence.Outbox;

namespace LotroKoniecDev.AuthSystem.API.Outbox;

/// <summary>
/// The only way to write an outbox message. It sets the row's <c>Type</c> to the contract's type name, so
/// no writer can mistype the string the registry and the routing table look up.
/// </summary>
/// <remarks>
/// <see cref="Enqueue{TMessage}"/> only adds the row; the caller commits it with its own change, and
/// <see cref="OutboxWakeUpInterceptor"/> then wakes the relay (ADR-0038 §6).
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

        // A contract with no route would commit and fail only later in the relay; this fails the writer's
        // own request at once. Its own exception type, so a writer's catch block does not swallow it.
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
