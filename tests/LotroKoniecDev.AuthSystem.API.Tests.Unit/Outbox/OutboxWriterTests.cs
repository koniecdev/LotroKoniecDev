using LotroKoniecDev.AuthSystem.API.Outbox;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Outbox;

/// <summary>
/// Pins the check <see cref="OutboxWriter"/> does when a row is written: a contract nobody added to
/// <see cref="OutboxMessageRouting"/> must fail the writer's own request as soon as it is enqueued.
/// It uses its own exception type, because writers catch broad exception types for safety, for example
/// <c>RegisterUser</c> catches <see cref="InvalidOperationException"/> for an Identity lookup race, and
/// this crash must never be turned into a false business outcome.
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
