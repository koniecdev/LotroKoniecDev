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

    [Fact]
    public void TryGetRoutingKey_EmailChangeRequested_MapsToChangeRequestedRoutingKey()
    {
        bool found = OutboxMessageRouting.TryGetRoutingKey(
            nameof(EmailChangeRequested), out string? routingKey);

        found.ShouldBeTrue();
        routingKey.ShouldBe(RabbitMqTopology.EmailChangeRequestedRoutingKey);
    }

    [Fact]
    public void TryGetRoutingKey_EmailChangeCompleted_MapsToChangeCompletedRoutingKey()
    {
        bool found = OutboxMessageRouting.TryGetRoutingKey(
            nameof(EmailChangeCompleted), out string? routingKey);

        found.ShouldBeTrue();
        routingKey.ShouldBe(RabbitMqTopology.EmailChangeCompletedRoutingKey);
    }

    [Fact]
    public void EveryRoutingKey_MatchesTheQueueBindingPattern()
    {
        // The queue binds "email.#", so a key that does not start with "email." would publish into a
        // topic exchange with nothing bound to it — and a topic exchange drops those without a word.
        string[] routingKeys =
        [
            RabbitMqTopology.EmailConfirmationRoutingKey,
            RabbitMqTopology.PasswordResetRoutingKey,
            RabbitMqTopology.DeletionScheduledRoutingKey,
            RabbitMqTopology.DeletionCancelledRoutingKey,
            RabbitMqTopology.EmailChangeRequestedRoutingKey,
            RabbitMqTopology.EmailChangeCompletedRoutingKey
        ];

        string bindingPrefix = RabbitMqTopology.EmailBindingPattern.TrimEnd('#');

        routingKeys.ShouldAllBe(routingKey => routingKey.StartsWith(bindingPrefix, StringComparison.Ordinal));
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
    [InlineData("emailchangerequested")]
    [InlineData("email.change-requested")]
    [InlineData("emailchangecompleted")]
    [InlineData("email.change-completed")]
    [InlineData("SomeFutureUnmappedEvent")]
    public void TryGetRoutingKey_UnknownOrMiscasedType_ReturnsFalse(string type)
    {
        bool found = OutboxMessageRouting.TryGetRoutingKey(type, out string? routingKey);

        found.ShouldBeFalse();
        routingKey.ShouldBeNull();
    }
}
