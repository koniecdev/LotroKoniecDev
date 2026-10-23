namespace LotroKoniecDev.AuthSystem.API.Outbox;

internal sealed record OutboxRoute(string Exchange, string RoutingKey);
