using System.Text;
using LotroKoniecDev.SharedKernel.IntegrationEvents;
using LotroKoniecDev.TranslationSystem.API.Messaging;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Messaging;

/// <summary>
/// Pins what the consumer promises only in prose: no pause outlives the broker's consumer timeout, the
/// pauses only grow, and a payload it can never handle is refused instead of erasing a profile at
/// random.
/// </summary>
public sealed class AccountErasedConsumerTests
{
    [Fact]
    public void RetryBackoffs_EveryPause_StaysUnderTheBrokerConsumerTimeout()
    {
        // RabbitMQ closes the channel when an ack takes longer than consumer_timeout, 30 minutes by
        // default, and compose does not change it. A lost channel counts against the delivery limit,
        // which a database outage must never reach.
        TimeSpan consumerTimeout = TimeSpan.FromMinutes(30);

        AccountErasedConsumer.RetryBackoffs.ShouldAllBe(pause => pause < consumerTimeout);
    }

    [Fact]
    public void RetryBackoffs_FromFirstToLast_NeverShrink()
    {
        TimeSpan[] pauses = AccountErasedConsumer.RetryBackoffs;

        pauses.ShouldNotBeEmpty();
        pauses.Zip(pauses.Skip(1)).ShouldAllBe(pair => pair.First <= pair.Second);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"IdentityUserId":"00000000-0000-0000-0000-000000000000"}""")]
    [InlineData("""{"IdentityUserId":"not-a-guid"}""")]
    [InlineData("""{"IdentityUserId":42}""")]
    public void TryDeserialize_WithUnusablePayload_ReturnsNull(string payload)
    {
        // Act
        AccountErased? message = AccountErasedConsumer.TryDeserialize(Encoding.UTF8.GetBytes(payload));

        // Assert
        message.ShouldBeNull();
    }

    [Fact]
    public void TryDeserialize_WithThePayloadTheAuthSystemWrites_ReturnsTheAccountId()
    {
        // Arrange: the outbox writer serializes the shared record with the default options.
        Guid identityUserId = Guid.CreateVersion7();
        string payload = System.Text.Json.JsonSerializer.Serialize(new AccountErased(identityUserId));

        // Act
        AccountErased? message = AccountErasedConsumer.TryDeserialize(Encoding.UTF8.GetBytes(payload));

        // Assert
        message.ShouldNotBeNull();
        message.IdentityUserId.ShouldBe(identityUserId);
    }
}
