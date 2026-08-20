using LotroKoniecDev.AuthSystem.API.Outbox;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Outbox;

/// <summary>
/// Pins the write-time routability guard of <see cref="OutboxWriter"/>: a contract nobody mapped
/// in <see cref="OutboxMessageRouting"/> must fail the writer's own request the moment it is
/// enqueued — and with a dedicated exception type, because writers defensively filter broad
/// exception families (<c>RegisterUser</c> catches <see cref="InvalidOperationException"/> for an
/// Identity lookup race) and the crash must never be swallowed into a bogus business outcome.
/// </summary>
public sealed class OutboxWriterTests
{
    private sealed record ContractNobodyRouted(Guid UserId);

    [Fact]
    public void Enqueue_TypeWithoutRoutingKey_ThrowsBeforeTouchingTheUnitOfWork()
    {
        // Arrange: the guard must fire before any dependency is used, so the writer is built
        // with a null context on purpose: reaching the database would NRE instead of throwing
        using OutboxSignal outboxSignal = new();
        OutboxWriter sut = new(db: null!, outboxSignal, TimeProvider.System);

        // Act
        Action enqueue = () => sut.Enqueue(new ContractNobodyRouted(Guid.CreateVersion7()));

        // Assert
        UnroutableOutboxMessageTypeException exception =
            Should.Throw<UnroutableOutboxMessageTypeException>(enqueue);
        exception.Message.ShouldContain(nameof(ContractNobodyRouted));
        exception.ShouldNotBeAssignableTo<InvalidOperationException>();
    }
}
