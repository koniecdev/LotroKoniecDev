namespace LotroKoniecDev.Tests.Shared;

/// <summary>
/// The RabbitMQ image every test broker starts from. The file is linked into each suite that starts a
/// real broker, so a version bump cannot miss a fixture.
/// </summary>
/// <remarks>
/// It is the version <c>compose.yaml</c>, <c>compose.prod.yaml</c> and <c>compose.hetzner.yaml</c> run,
/// without the management UI, which a test does not need. Those files point back here, so an upgrade
/// changes all of them together.
/// </remarks>
internal static class RabbitMqImage
{
    public const string Name = "rabbitmq:4.3.4-alpine";
}
