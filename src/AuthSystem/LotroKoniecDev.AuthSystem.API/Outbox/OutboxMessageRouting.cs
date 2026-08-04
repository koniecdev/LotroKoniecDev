using System.Diagnostics.CodeAnalysis;
using LotroKoniecDev.AuthSystem.Infrastructure.Messaging;

namespace LotroKoniecDev.AuthSystem.API.Outbox;

/// <summary>
/// Maps an outbox row's <c>Type</c> — the payload contract name the consumer deserializes by —
/// to the broker routing key it travels under. The two stay separate concepts on purpose: the
/// type says what the payload is, the routing key says which bindings receive it, and conflating
/// them would let a contract rename silently unroute messages from a live queue.
/// </summary>
internal static class OutboxMessageRouting
{
    private static readonly Dictionary<string, string> RoutingKeysByType = new(StringComparer.Ordinal)
    {
        [nameof(EmailConfirmationRequested)] = RabbitMqTopology.EmailConfirmationRoutingKey
    };

    public static bool TryGetRoutingKey(string type, [NotNullWhen(true)] out string? routingKey)
    {
        return RoutingKeysByType.TryGetValue(type, out routingKey);
    }
}
