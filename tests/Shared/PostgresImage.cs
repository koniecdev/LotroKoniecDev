namespace LotroKoniecDev.Tests.Shared;

/// <summary>
/// The PostgreSQL image every test database starts from (#1002). The file is linked into each suite that
/// starts a real database, so a version bump is one edit and cannot miss a fixture.
/// </summary>
/// <remarks>
/// Keep it on the same major version as <c>compose.yaml</c>, <c>compose.prod.yaml</c> and the Neon database
/// that runs production (ADR-0014). The N-1 proof (ADR-0024) runs through these fixtures too, so a test
/// database on another version proves each migration on a version that production never runs.
/// </remarks>
internal static class PostgresImage
{
    public const string Name = "postgres:18-alpine";
}
