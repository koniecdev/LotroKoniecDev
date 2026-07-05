using Shouldly;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.N1Compat;

/// <summary>
/// Pure unit coverage for the N-1 seam's schema-script parser (ADR-0024 / #340). It lives in the
/// integration project because the seam is internal to it; it instantiates no factory and starts
/// no container. The parser is the seam's vacuity guard: if it finds no history inserts in a
/// generated script, the seam throws instead of letting the suite silently migrate its own
/// (old) schema.
/// </summary>
public sealed class N1CompatSchemaSeamTests
{
    private const string IdempotentScriptWithTwoMigrations =
        """
        CREATE TABLE IF NOT EXISTS translation."__EFMigrationsHistory" (
            "MigrationId" character varying(150) NOT NULL,
            "ProductVersion" character varying(32) NOT NULL,
            CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
        );

        DO $EF$
        BEGIN
            IF NOT EXISTS(SELECT 1 FROM translation."__EFMigrationsHistory" WHERE "MigrationId" = '20260612201021_InitialCreate') THEN
            CREATE TABLE translation."GameVersions" ("Id" uuid NOT NULL);
            END IF;
        END $EF$;

        DO $EF$
        BEGIN
            IF NOT EXISTS(SELECT 1 FROM translation."__EFMigrationsHistory" WHERE "MigrationId" = '20260612201021_InitialCreate') THEN
            INSERT INTO translation."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
            VALUES ('20260612201021_InitialCreate', '10.0.0');
            END IF;
        END $EF$;

        DO $EF$
        BEGIN
            IF NOT EXISTS(SELECT 1 FROM translation."__EFMigrationsHistory" WHERE "MigrationId" = '20260613090456_AddTranslationAggregate') THEN
            INSERT INTO translation."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
            VALUES ('20260613090456_AddTranslationAggregate', '10.0.0');
            END IF;
        END $EF$;
        """;

    [Fact]
    public void ParseHistoryInserts_IdempotentScriptWithTwoMigrations_ReturnsBothIdsWithTheirTable()
    {
        IReadOnlyCollection<HistoryInsert> inserts = N1CompatSchemaSeam.ParseHistoryInserts(IdempotentScriptWithTwoMigrations);

        inserts.Count.ShouldBe(2);
        inserts.ShouldContain(new HistoryInsert("translation.\"__EFMigrationsHistory\"", "20260612201021_InitialCreate"));
        inserts.ShouldContain(new HistoryInsert("translation.\"__EFMigrationsHistory\"", "20260613090456_AddTranslationAggregate"));
    }

    [Fact]
    public void ParseHistoryInserts_IdempotencyGuardConditionsOnly_ReturnsEmpty()
    {
        string script =
            """
            DO $EF$
            BEGIN
                IF NOT EXISTS(SELECT 1 FROM translation."__EFMigrationsHistory" WHERE "MigrationId" = '20260612201021_InitialCreate') THEN
                CREATE TABLE translation."GameVersions" ("Id" uuid NOT NULL);
                END IF;
            END $EF$;
            """;

        IReadOnlyCollection<HistoryInsert> inserts = N1CompatSchemaSeam.ParseHistoryInserts(script);

        inserts.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("-- an entirely unrelated script\nSELECT 1;")]
    public void ParseHistoryInserts_NoHistoryInserts_ReturnsEmpty(string script)
    {
        IReadOnlyCollection<HistoryInsert> inserts = N1CompatSchemaSeam.ParseHistoryInserts(script);

        inserts.ShouldBeEmpty();
    }

    [Fact]
    public void ParseHistoryInserts_UnqualifiedHistoryTable_CapturesTheBareTableName()
    {
        string script =
            """
            INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
            VALUES ('20260101000000_Init', '10.0.0');
            """;

        IReadOnlyCollection<HistoryInsert> inserts = N1CompatSchemaSeam.ParseHistoryInserts(script);

        inserts.ShouldHaveSingleItem();
        inserts.ShouldContain(new HistoryInsert("\"__EFMigrationsHistory\"", "20260101000000_Init"));
    }

    [Fact]
    public void ParseHistoryInserts_SingleLineInsert_IsParsed()
    {
        string script =
            """INSERT INTO auth."__EFMigrationsHistory" ("MigrationId", "ProductVersion") VALUES ('20260627145210_InitialAuthSchema', '10.0.0');""";

        IReadOnlyCollection<HistoryInsert> inserts = N1CompatSchemaSeam.ParseHistoryInserts(script);

        inserts.ShouldHaveSingleItem();
        inserts.ShouldContain(new HistoryInsert("auth.\"__EFMigrationsHistory\"", "20260627145210_InitialAuthSchema"));
    }

    [Fact]
    public void ParseHistoryInserts_DuplicatedInsertStatements_AreDeduplicated()
    {
        string insert =
            """
            INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
            VALUES ('20260101000000_Init', '10.0.0');
            """;
        string script = insert + "\n" + insert;

        IReadOnlyCollection<HistoryInsert> inserts = N1CompatSchemaSeam.ParseHistoryInserts(script);

        inserts.ShouldHaveSingleItem();
    }

    [Fact]
    public void ParseHistoryInserts_InsertIntoAnotherTable_IsIgnored()
    {
        string script =
            """
            INSERT INTO translation."Translations" ("MigrationId", "ProductVersion")
            VALUES ('not-a-migration', '10.0.0');
            """;

        IReadOnlyCollection<HistoryInsert> inserts = N1CompatSchemaSeam.ParseHistoryInserts(script);

        inserts.ShouldBeEmpty();
    }
}
