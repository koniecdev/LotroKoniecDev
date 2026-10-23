namespace LotroKoniecDev.Tests.Shared;

/// <summary>
/// Linked into each suite that starts a real database, so a version bump cannot miss a fixture (#1002).
/// </summary>
/// <remarks>
/// Keep it on the same major version as <c>compose.yaml</c>, <c>compose.prod.yaml</c> and the Neon
/// database that runs production (ADR-0014), or each migration is tested on a version production never
/// runs. The N-1 proof (ADR-0024) uses the previous release's copy of this file, so a bump reaches it one
/// release later. The version is also named in <c>.github/workflows/ci.yml</c> and <c>tests/CLAUDE.md</c>.
/// </remarks>
internal static class PostgresImage
{
    public const string Name = "postgres:18-alpine";
}
