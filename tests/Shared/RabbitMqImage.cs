namespace LotroKoniecDev.Tests.Shared;

/// <summary>
/// Linked into each suite that starts a real broker, so a version bump cannot miss a fixture.
/// </summary>
/// <remarks>
/// The version <c>compose.yaml</c>, <c>compose.prod.yaml</c> and <c>compose.hetzner.yaml</c> run, minus
/// the management UI a test does not need. Bump all four together.
/// </remarks>
internal static class RabbitMqImage
{
    public const string Name = "rabbitmq:4.3.4-alpine";
}
