using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using LotroKoniecDev.SharedKernel.IntegrationEvents;
using LotroKoniecDev.SharedKernel.Messaging;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.API.Features.Translators;
using LotroKoniecDev.TranslationSystem.API.Messaging;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Messaging;

/// <summary>
/// Pins what the consumer promises only in prose: no pause outlives the broker's consumer timeout, the
/// pauses only grow, a payload it can never handle is refused instead of erasing a profile at random,
/// and a channel that fails under it never leaves a delivery stuck.
/// </summary>
public sealed class AccountErasedConsumerTests
{
    private const ulong DeliveryTag = 42;

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
        string payload = JsonSerializer.Serialize(new AccountErased(identityUserId));

        // Act
        AccountErased? message = AccountErasedConsumer.TryDeserialize(Encoding.UTF8.GetBytes(payload));

        // Assert
        message.ShouldNotBeNull();
        message.IdentityUserId.ShouldBe(identityUserId);
    }

    [Fact]
    public async Task OnDeliveredAsync_WhenTheAckFails_ShouldPutTheDeliveryBackWithAReject()
    {
        // Arrange: the profile is erased, then the channel fails under the ack.
        IChannel channel = Substitute.For<IChannel>();
        channel.BasicAckAsync(DeliveryTag, false, Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromException(new InvalidOperationException("channel closed")));
        AccountErasedConsumer consumer = CreateConsumer();

        // Act
        await consumer.OnDeliveredAsync(channel, Delivery(), CancellationToken.None);

        // Assert: a reject, so a message that keeps breaking the consumer ends at the delivery limit.
        await channel.Received(1).BasicRejectAsync(DeliveryTag, true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnDeliveredAsync_WhenTheAckAndTheRejectBothFail_ShouldNotThrow()
    {
        // Arrange: a dead channel. An exception leaving the handler would be swallowed by the client
        // library; the broker puts the unacknowledged delivery back when the channel closes.
        IChannel channel = Substitute.For<IChannel>();
        channel.BasicAckAsync(DeliveryTag, false, Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromException(new InvalidOperationException("channel closed")));
        channel.BasicRejectAsync(DeliveryTag, true, Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromException(new InvalidOperationException("channel closed")));
        AccountErasedConsumer consumer = CreateConsumer();

        // Act & Assert
        await Should.NotThrowAsync(() => consumer.OnDeliveredAsync(channel, Delivery(), CancellationToken.None));
    }

    private static AccountErasedConsumer CreateConsumer()
    {
        ServiceProvider services = new ServiceCollection()
            .AddScoped<ICommandHandler<EraseTranslatorProfile.Command, Result>, SucceedingEraseHandler>()
            .BuildServiceProvider();

        return new AccountErasedConsumer(
            Microsoft.Extensions.Options.Options.Create(new RabbitMqSettings
            {
                Host = "localhost",
                Username = "rabbitmq",
                Password = "a-broker-password"
            }),
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AccountErasedConsumer>.Instance,
            [TimeSpan.Zero]);
    }

    private static BasicDeliverEventArgs Delivery()
    {
        BasicProperties properties = new()
        {
            MessageId = Guid.NewGuid().ToString(),
            Type = nameof(AccountErased)
        };
        byte[] body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new AccountErased(Guid.NewGuid())));

        return new BasicDeliverEventArgs(
            consumerTag: "test",
            deliveryTag: DeliveryTag,
            redelivered: false,
            exchange: AccountEvents.Exchange,
            routingKey: AccountEvents.ErasedRoutingKey,
            properties: properties,
            body: body);
    }

    /// <summary>
    /// The erasure succeeds, so the only failure in these tests is the channel's.
    /// </summary>
    private sealed class SucceedingEraseHandler : ICommandHandler<EraseTranslatorProfile.Command, Result>
    {
        public ValueTask<Result> Handle(EraseTranslatorProfile.Command command, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Success());
    }
}
