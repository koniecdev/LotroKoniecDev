using LotroKoniecDev.AuthSystem.API.BackgroundServices;
using LotroKoniecDev.AuthSystem.Infrastructure.Messaging;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Messaging;

/// <summary>
/// Pins the two invariants the redelivery backoff ladder promises only in prose (ADR-0036):
/// the ladder and <see cref="RabbitMqTopology.EmailDeliveryLimit"/> move together, and no rung
/// may hold a delivery unacked long enough for the broker to kill the channel. Without these,
/// <c>Math.Min</c> in the consumer silently absorbs any drift.
/// </summary>
public sealed class EmailConfirmationConsumerTests
{
    [Fact]
    public void RedeliveryBackoffs_ComparedToDeliveryLimit_HasOneEntryPerAllowedRedelivery()
    {
        EmailConfirmationConsumer.RedeliveryBackoffs.Length.ShouldBe(RabbitMqTopology.EmailDeliveryLimit);
    }

    [Fact]
    public void RedeliveryBackoffs_EveryPause_StaysUnderTheBrokerConsumerTimeout()
    {
        // The broker kills the channel when an ack takes longer than consumer_timeout (30 min
        // RabbitMQ default; compose does not override it) — a rung at or past it would turn a
        // pause into a channel loss that burns a redelivery instead of waiting one out.
        TimeSpan consumerTimeout = TimeSpan.FromMinutes(30);

        EmailConfirmationConsumer.RedeliveryBackoffs.ShouldAllBe(pause => pause < consumerTimeout);
    }
}
