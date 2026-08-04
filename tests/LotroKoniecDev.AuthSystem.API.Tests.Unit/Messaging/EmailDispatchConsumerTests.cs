using LotroKoniecDev.AuthSystem.API.BackgroundServices;
using LotroKoniecDev.AuthSystem.Infrastructure.Messaging;
using RabbitMQ.Client;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Messaging;

/// <summary>
/// Pins the two invariants the redelivery backoff ladder promises only in prose (ADR-0036):
/// the ladder and <see cref="RabbitMqTopology.EmailDeliveryLimit"/> move together, and no rung
/// may hold a delivery unacked long enough for the broker to kill the channel. Without these,
/// <c>Math.Min</c> in the consumer silently absorbs any drift. Also pins the poison decision for
/// unusable message ids (ADR-0037): a delivery the inbox cannot deduplicate must be rejected,
/// not processed blind.
/// </summary>
public sealed class EmailDispatchConsumerTests
{
    [Fact]
    public void RedeliveryBackoffs_ComparedToDeliveryLimit_HasOneEntryPerAllowedRedelivery()
    {
        EmailDispatchConsumer.RedeliveryBackoffs.Length.ShouldBe(RabbitMqTopology.EmailDeliveryLimit);
    }

    [Fact]
    public void RedeliveryBackoffs_EveryPause_StaysUnderTheBrokerConsumerTimeout()
    {
        // The broker kills the channel when an ack takes longer than consumer_timeout (30 min
        // RabbitMQ default; compose does not override it) — a rung at or past it would turn a
        // pause into a channel loss that burns a redelivery instead of waiting one out.
        TimeSpan consumerTimeout = TimeSpan.FromMinutes(30);

        EmailDispatchConsumer.RedeliveryBackoffs.ShouldAllBe(pause => pause < consumerTimeout);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void TryReadMessageId_WithUnusableValue_ReturnsFalse(string? rawMessageId)
    {
        // Arrange — BasicProperties implements IReadOnlyBasicProperties
        BasicProperties properties = new() { MessageId = rawMessageId };

        // Act
        bool usable = EmailDispatchConsumer.TryReadMessageId(properties, out Guid _);

        // Assert
        usable.ShouldBeFalse();
    }

    [Fact]
    public void TryReadMessageId_WithGuidValue_ReturnsTheParsedId()
    {
        // Arrange
        Guid messageId = Guid.CreateVersion7();
        BasicProperties properties = new() { MessageId = messageId.ToString() };

        // Act
        bool usable = EmailDispatchConsumer.TryReadMessageId(properties, out Guid parsed);

        // Assert
        usable.ShouldBeTrue();
        parsed.ShouldBe(messageId);
    }
}
