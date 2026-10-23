using LotroKoniecDev.AuthSystem.API.Outbox;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Outbox;

/// <summary>
/// A contract nobody added to <see cref="OutboxMessageRouting"/> fails the writer's own request at enqueue,
/// with an exception type no writer catches, so the crash never turns into a false business outcome.
/// </summary>
public sealed class OutboxWriterTests
{
    private sealed record ContractNobodyRouted(Guid UserId);

    [Fact]
    public void Enqueue_TypeWithoutRoutingKey_ThrowsBeforeTouchingTheUnitOfWork()
    {
        // Arrange: the guard must fire before any dependency is used, so the writer gets a null context.
        // Reaching the database would throw a NullReferenceException instead
        OutboxWriter sut = new(db: null!, TimeProvider.System);

        // Act
        Action enqueue = () => sut.Enqueue(new ContractNobodyRouted(Guid.CreateVersion7()));

        // Assert
        UnroutableOutboxMessageTypeException exception =
            Should.Throw<UnroutableOutboxMessageTypeException>(enqueue);
        exception.Message.ShouldContain(nameof(ContractNobodyRouted));
        exception.ShouldNotBeAssignableTo<InvalidOperationException>();
    }
}
