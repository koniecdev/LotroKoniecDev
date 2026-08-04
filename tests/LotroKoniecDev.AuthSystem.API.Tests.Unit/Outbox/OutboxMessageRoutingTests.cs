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

    [Theory]
    [InlineData("")]
    [InlineData("emailconfirmationrequested")]
    [InlineData("email.confirmation")]
    [InlineData("passwordresetrequested")]
    [InlineData("email.password-reset")]
    [InlineData("SomeFutureUnmappedEvent")]
    public void TryGetRoutingKey_UnknownOrMiscasedType_ReturnsFalse(string type)
    {
        bool found = OutboxMessageRouting.TryGetRoutingKey(type, out string? routingKey);

        found.ShouldBeFalse();
        routingKey.ShouldBeNull();
    }
}
