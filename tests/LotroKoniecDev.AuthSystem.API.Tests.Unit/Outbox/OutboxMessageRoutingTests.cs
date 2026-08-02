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

    [Theory]
    [InlineData("")]
    [InlineData("emailconfirmationrequested")]
    [InlineData("email.confirmation")]
    [InlineData("SomeFutureUnmappedEvent")]
    public void TryGetRoutingKey_UnknownOrMiscasedType_ReturnsFalse(string type)
    {
        bool found = OutboxMessageRouting.TryGetRoutingKey(type, out string? routingKey);

        found.ShouldBeFalse();
        routingKey.ShouldBeNull();
    }
}
