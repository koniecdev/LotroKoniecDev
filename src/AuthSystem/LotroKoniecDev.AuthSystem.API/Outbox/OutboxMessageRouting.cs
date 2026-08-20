using System.Diagnostics.CodeAnalysis;
using LotroKoniecDev.AuthSystem.Infrastructure.Messaging;

namespace LotroKoniecDev.AuthSystem.API.Outbox;

/// <summary>
/// Maps an outbox row's <c>Type</c>, the payload contract name the consumer reads it back by, to the
/// broker routing key it travels under. The two stay separate on purpose: the type says what the
/// payload is, the routing key says which bindings receive it. If they were the same thing, renaming
/// a contract would quietly stop messages reaching a live queue.
/// </summary>
internal static class OutboxMessageRouting
{
    private static readonly Dictionary<string, string> RoutingKeysByType = new(StringComparer.Ordinal)
    {
        [nameof(EmailConfirmationRequested)] = RabbitMqTopology.EmailConfirmationRoutingKey,
        [nameof(PasswordResetRequested)] = RabbitMqTopology.PasswordResetRoutingKey,
        [nameof(AccountDeletionScheduled)] = RabbitMqTopology.DeletionScheduledRoutingKey,
        [nameof(AccountDeletionCancelled)] = RabbitMqTopology.DeletionCancelledRoutingKey,
        [nameof(EmailChangeRequested)] = RabbitMqTopology.EmailChangeRequestedRoutingKey,
        [nameof(EmailChangeCompleted)] = RabbitMqTopology.EmailChangeCompletedRoutingKey
    };

    public static bool TryGetRoutingKey(string type, [NotNullWhen(true)] out string? routingKey)
    {
        return RoutingKeysByType.TryGetValue(type, out routingKey);
    }
}
