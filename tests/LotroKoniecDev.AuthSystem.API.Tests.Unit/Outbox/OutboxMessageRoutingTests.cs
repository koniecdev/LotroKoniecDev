using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.Infrastructure.Messaging;
using LotroKoniecDev.SharedKernel.IntegrationEvents;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Outbox;

public sealed class OutboxMessageRoutingTests
{
    [Fact]
    public void TryGetRoute_EmailConfirmationRequested_MapsToTheEmailsExchangeAndConfirmationRoutingKey()
    {
        bool found = OutboxMessageRouting.TryGetRoute(
            nameof(EmailConfirmationRequested), out OutboxRoute? route);

        found.ShouldBeTrue();
        route.ShouldBe(new OutboxRoute(RabbitMqTopology.EmailsExchange, RabbitMqTopology.EmailConfirmationRoutingKey));
    }

    [Fact]
    public void TryGetRoute_PasswordResetRequested_MapsToTheEmailsExchangeAndPasswordResetRoutingKey()
    {
        bool found = OutboxMessageRouting.TryGetRoute(
            nameof(PasswordResetRequested), out OutboxRoute? route);

        found.ShouldBeTrue();
        route.ShouldBe(new OutboxRoute(RabbitMqTopology.EmailsExchange, RabbitMqTopology.PasswordResetRoutingKey));
    }

    [Fact]
    public void TryGetRoute_AccountDeletionScheduled_MapsToTheEmailsExchangeAndDeletionScheduledRoutingKey()
    {
        bool found = OutboxMessageRouting.TryGetRoute(
            nameof(AccountDeletionScheduled), out OutboxRoute? route);

        found.ShouldBeTrue();
        route.ShouldBe(new OutboxRoute(RabbitMqTopology.EmailsExchange, RabbitMqTopology.DeletionScheduledRoutingKey));
    }

    [Fact]
    public void TryGetRoute_AccountDeletionCancelled_MapsToTheEmailsExchangeAndDeletionCancelledRoutingKey()
    {
        bool found = OutboxMessageRouting.TryGetRoute(
            nameof(AccountDeletionCancelled), out OutboxRoute? route);

        found.ShouldBeTrue();
        route.ShouldBe(new OutboxRoute(RabbitMqTopology.EmailsExchange, RabbitMqTopology.DeletionCancelledRoutingKey));
    }

    [Fact]
    public void TryGetRoute_EmailChangeRequested_MapsToTheEmailsExchangeAndChangeRequestedRoutingKey()
    {
        bool found = OutboxMessageRouting.TryGetRoute(
            nameof(EmailChangeRequested), out OutboxRoute? route);

        found.ShouldBeTrue();
        route.ShouldBe(new OutboxRoute(RabbitMqTopology.EmailsExchange, RabbitMqTopology.EmailChangeRequestedRoutingKey));
    }

    [Fact]
    public void TryGetRoute_EmailChangeCompleted_MapsToTheEmailsExchangeAndChangeCompletedRoutingKey()
    {
        bool found = OutboxMessageRouting.TryGetRoute(
            nameof(EmailChangeCompleted), out OutboxRoute? route);

        found.ShouldBeTrue();
        route.ShouldBe(new OutboxRoute(RabbitMqTopology.EmailsExchange, RabbitMqTopology.EmailChangeCompletedRoutingKey));
    }

    [Fact]
    public void TryGetRoute_AccountErased_MapsToTheAccountEventsExchange()
    {
        bool found = OutboxMessageRouting.TryGetRoute(nameof(AccountErased), out OutboxRoute? route);

        found.ShouldBeTrue();
        route.ShouldBe(new OutboxRoute("lotro.accounts", "account.erased"));
    }

    [Fact]
    public void EveryRoutedContract_NamesItsAccountIdentityUserId()
    {
        // The erasure reconciler finds an erased account's messages by this key (ADR-0065). A contract
        // that named its account another way would keep its personal data after the erasure.
        Type[] contracts = typeof(OutboxWriter).Assembly.GetTypes()
            .Concat(typeof(AccountErased).Assembly.GetTypes())
            .Where(type => OutboxMessageRouting.TryGetRoute(type.Name, out _))
            .ToArray();

        contracts.Length.ShouldBe(7);
        contracts.ShouldAllBe(type => type.GetProperty("IdentityUserId") != null
                                      && type.GetProperty("IdentityUserId")!.PropertyType == typeof(Guid));
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
    [InlineData("accounterased")]
    [InlineData("account.erased")]
    [InlineData("SomeFutureUnmappedEvent")]
    public void TryGetRoute_UnknownOrMiscasedType_ReturnsFalse(string type)
    {
        bool found = OutboxMessageRouting.TryGetRoute(type, out OutboxRoute? route);

        found.ShouldBeFalse();
        route.ShouldBeNull();
    }
}
