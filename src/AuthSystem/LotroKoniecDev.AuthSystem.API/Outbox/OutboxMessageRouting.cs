using System.Diagnostics.CodeAnalysis;
using LotroKoniecDev.AuthSystem.Infrastructure.Messaging;
using LotroKoniecDev.SharedKernel.IntegrationEvents;

namespace LotroKoniecDev.AuthSystem.API.Outbox;

/// <summary>
/// Maps an outbox row's <c>Type</c>, the payload contract name the consumer reads it back by, to the
/// exchange and routing key it travels under. The two stay separate on purpose: the type says what the
/// payload is, the route says which bindings receive it. If they were the same thing, renaming a
/// contract would quietly stop messages reaching a live queue.
/// </summary>
internal static class OutboxMessageRouting
{
    private static readonly Dictionary<string, OutboxRoute> RoutesByType = new(StringComparer.Ordinal)
    {
        [nameof(EmailConfirmationRequested)] = Email(RabbitMqTopology.EmailConfirmationRoutingKey),
        [nameof(PasswordResetRequested)] = Email(RabbitMqTopology.PasswordResetRoutingKey),
        [nameof(AccountDeletionScheduled)] = Email(RabbitMqTopology.DeletionScheduledRoutingKey),
        [nameof(AccountDeletionCancelled)] = Email(RabbitMqTopology.DeletionCancelledRoutingKey),
        [nameof(EmailChangeRequested)] = Email(RabbitMqTopology.EmailChangeRequestedRoutingKey),
        [nameof(EmailChangeCompleted)] = Email(RabbitMqTopology.EmailChangeCompletedRoutingKey),

        // The one message for another context: the TMS erases its translator profile (ADR-0065).
        [nameof(AccountErased)] = new(AccountEvents.Exchange, AccountEvents.ErasedRoutingKey)
    };

    public static bool TryGetRoute(string type, [NotNullWhen(true)] out OutboxRoute? route)
    {
        return RoutesByType.TryGetValue(type, out route);
    }

    private static OutboxRoute Email(string routingKey) => new(RabbitMqTopology.EmailsExchange, routingKey);
}
