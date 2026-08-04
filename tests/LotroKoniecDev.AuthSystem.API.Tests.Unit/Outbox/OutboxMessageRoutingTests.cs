using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.Infrastructure.Messaging;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Outbox;

public sealed class OutboxMessageRoutingTests
{
    [Fact]
    public void TryGetRoutingKey_EmailConfirmationRequested_MapsToConfirmationRoutingKey()
    {
        bool found = OutboxMessageRouting.TryGetRoutingKey(
            nameof(EmailConfirmationRequested), out string? routingKey);

        found.ShouldBeTrue();
        routingKey.ShouldBe(RabbitMqTopology.EmailConfirmationRoutingKey);
    }

    [Fact]
    public void TryGetRoutingKey_PasswordResetRequested_MapsToPasswordResetRoutingKey()
    {
        bool found = OutboxMessageRouting.TryGetRoutingKey(
            nameof(PasswordResetRequested), out string? routingKey);

        found.ShouldBeTrue();
        routingKey.ShouldBe(RabbitMqTopology.PasswordResetRoutingKey);
    }

    [Fact]
    public void TryGetRoutingKey_AccountDeletionScheduled_MapsToDeletionScheduledRoutingKey()
    {
        bool found = OutboxMessageRouting.TryGetRoutingKey(
            nameof(AccountDeletionScheduled), out string? routingKey);

        found.ShouldBeTrue();
        routingKey.ShouldBe(RabbitMqTopology.DeletionScheduledRoutingKey);
    }

    [Fact]
    public void TryGetRoutingKey_AccountDeletionCancelled_MapsToDeletionCancelledRoutingKey()
    {
        bool found = OutboxMessageRouting.TryGetRoutingKey(
            nameof(AccountDeletionCancelled), out string? routingKey);

        found.ShouldBeTrue();
        routingKey.ShouldBe(RabbitMqTopology.DeletionCancelledRoutingKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("emailconfirmationrequested")]
    [InlineData("email.confirmation")]
    [InlineData("passwordresetrequested")]
    [InlineData("email.password-reset")]
    [InlineData("accountdeletionscheduled")]
    [InlineData("email.deletion-scheduled")]
    [InlineData("accountdeletioncancelled")]
    [InlineData("email.deletion-cancelled")]
    [InlineData("SomeFutureUnmappedEvent")]
    public void TryGetRoutingKey_UnknownOrMiscasedType_ReturnsFalse(string type)
    {
        bool found = OutboxMessageRouting.TryGetRoutingKey(type, out string? routingKey);

        found.ShouldBeFalse();
        routingKey.ShouldBeNull();
    }
}
