using LotroKoniecDev.AuthSystem.Persistence.Inbox;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Inbox;

public sealed class InboxMessageTests
{
    [Fact]
    public void Create_WithValidArguments_SetsMessageIdAndProcessedOn()
    {
        // Arrange
        Guid messageId = Guid.CreateVersion7();
        DateTimeOffset processedOn = new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);

        // Act
        InboxMessage message = InboxMessage.Create(messageId, processedOn);

        // Assert
        message.MessageId.ShouldBe(messageId);
        message.ProcessedOn.ShouldBe(processedOn);
    }

    [Fact]
    public void Create_WithEmptyMessageId_ThrowsArgumentException()
    {
        Should.Throw<ArgumentException>(() =>
            InboxMessage.Create(Guid.Empty, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Create_WithDefaultProcessedOn_ThrowsArgumentException()
    {
        Should.Throw<ArgumentException>(() =>
            InboxMessage.Create(Guid.CreateVersion7(), default));
    }
}
