using System.Text.Json;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.AuthSystem.Persistence.Outbox;

namespace LotroKoniecDev.AuthSystem.API.Outbox;

/// <summary>
/// The one way a feature slice writes an e-mail message to the outbox: serializes the payload,
/// stamps the row's <c>Type</c> with the contract's type name (the registry and routing key both
/// select on it, so a writer can never typo the string), and carries the after-commit nudge —
/// bundling the whole ADR-0035 §2 idiom in a single injected component so no future writer can
/// reinvent half of it (ADR-0038 decision 6 keeps the nudge an explicit call, not an interceptor).
/// </summary>
/// <remarks>
/// <see cref="Enqueue{TMessage}"/> only stages the row in the caller's unit of work — the caller
/// commits it together with its own state change, which is the outbox pattern's whole point.
/// After that commit (and only after: the relay reads committed rows, so a nudge sent inside the
/// transaction would race it into seeing nothing) the caller calls
/// <see cref="NotifyEnqueuedCommitted"/> exactly once.
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

        // Programmer-error guard: a contract nobody routed would commit fine and then jam the
        // relay (OutboxMessageRouting fails its row loudly, but only after the fact) — failing
        // the writer's own request surfaces the missing routing entry the moment it is written.
        // The dedicated exception type keeps it out of writers' defensive catch filters.
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
