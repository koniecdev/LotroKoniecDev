namespace LotroKoniecDev.AuthSystem.API.Outbox;

/// <summary>
/// Where one outbox message type goes on the broker. See <see cref="OutboxMessageRouting"/>.
/// </summary>
internal sealed record OutboxRoute(string Exchange, string RoutingKey);
