namespace LotroKoniecDev.Tests.Shared;

/// <summary>
/// The PostgreSQL image every test database starts from (#1002). The file is linked into each suite that
/// starts a real database, so a version bump cannot miss a fixture.
/// </summary>
/// <remarks>
/// Keep it on the same major version as <c>compose.yaml</c>, <c>compose.prod.yaml</c> and the Neon database
/// that runs production (ADR-0014). A test database on another version tests each migration on a version
/// that production never runs. The N-1 proof (ADR-0024) uses the previous release's copy of this file, so a
/// bump reaches that proof one release later. The version is also named in <c>.github/workflows/ci.yml</c>
/// and <c>tests/CLAUDE.md</c>, so a bump updates them too.
/// </remarks>
internal static class PostgresImage
{
    public const string Name = "postgres:18-alpine";
}
