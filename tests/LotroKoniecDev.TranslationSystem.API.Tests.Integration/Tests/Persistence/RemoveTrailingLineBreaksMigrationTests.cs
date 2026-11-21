using System.Reflection;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.WriteDbContexts;
using LotroKoniecDev.TranslationSystem.Persistence.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.Persistence;

/// <summary>
/// The #1476 cleaning, run through the context the prod migrator builds. A fresh database stops at the
/// step before it, rows are planted with raw SQL (the domain would already trim them), and then the
/// cleaning runs. The shared database stays untouched.
/// </summary>
[Collection("TranslationApi")]
public sealed class RemoveTrailingLineBreaksMigrationTests : IAsyncLifetime
{
    private const string DatabaseName = "remove_trailing_line_breaks_migration";
    private const int FileId = 620756992;
    private const string EnglishWithoutABreak = "Show all";
    private static readonly DateTimeOffset CreatedAt = new(2026, 11, 18, 1, 6, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset UpdatedAt = new(2026, 11, 19, 7, 27, 0, TimeSpan.Zero);
    private static readonly Guid VersionId = Guid.CreateVersion7();
    private static readonly Guid SubmitterId = Guid.CreateVersion7();
    private static readonly Guid ApproverId = Guid.CreateVersion7();

    private readonly TranslationSystemApiFactory _factory;
    private ApplicationWriteDbContext _dbContext = null!;

    public async Task InitializeAsync()
    {
        // xUnit skips DisposeAsync when InitializeAsync throws, so a failed run may leave the database.
        await ExecuteOnSharedDatabaseAsync($"DROP DATABASE IF EXISTS {DatabaseName} WITH (FORCE);");
        string sharedConnectionString = await ExecuteOnSharedDatabaseAsync($"CREATE DATABASE {DatabaseName};");
        string connectionString = new NpgsqlConnectionStringBuilder(sharedConnectionString) { Database = DatabaseName }
            .ConnectionString;

        _dbContext = new ApplicationWriteDbContextDesignTimeFactory().CreateDbContext(["--connection", connectionString]);
        await _dbContext.Database.MigrateAsync(MigrationId<AddTranslationSaveLedger>());

        await _dbContext.Database.ExecuteSqlAsync($"""
            INSERT INTO translation."GameVersions" ("Id", "LotroNotationVersion", "Status", "DetectedAt")
            VALUES ({VersionId}, '48.0', 'Processed', {CreatedAt});
            INSERT INTO translation."Translators" ("Id", "IdentityId", "DisplayName", "Email", "ProvisionedAt")
            VALUES ({SubmitterId}, {Guid.CreateVersion7()}, 'Submitter', NULL, {CreatedAt}),
                   ({ApproverId}, {Guid.CreateVersion7()}, 'Approver', NULL, {CreatedAt});
            """);
    }

    public async Task DisposeAsync()
    {
        string connectionString = _dbContext.Database.GetConnectionString()!;
        await _dbContext.DisposeAsync();

        await using (NpgsqlConnection pooled = new(connectionString))
        {
            NpgsqlConnection.ClearPool(pooled);
        }

        await ExecuteOnSharedDatabaseAsync($"DROP DATABASE {DatabaseName} WITH (FORCE);");
    }

    [Fact]
    public async Task Migrate_WhenTheEnglishEndsWithoutABreak_ShouldRemoveThePolishTrailingBreaks()
    {
        // Arrange
        await PlantAsync(1, EnglishWithoutABreak, "Pokaż wszystko\r\n");
        await PlantAsync(2, EnglishWithoutABreak, "Tylko LF\n");
        await PlantAsync(3, EnglishWithoutABreak, "Tylko CR\r");
        await PlantAsync(4, EnglishWithoutABreak, "Dwa razy\r\n\r\n");
        await PlantAsync(5, EnglishWithoutABreak, "Spacja \r\n");
        // The game writes its line break as the two characters \n, which the translator copies.
        await PlantAsync(6, "Line one\\n", "Linia pierwsza\\n\r\n");
        await PlantAsync(7, "", "Pusty angielski\r\n");

        // Act
        await _dbContext.Database.MigrateAsync(MigrationId<RemoveTrailingLineBreaksFromTranslatedText>());

        // Assert
        Dictionary<long, StoredRow> rows = await ReadRowsAsync();
        rows[1].TranslatedText.ShouldBe("Pokaż wszystko");
        rows[2].TranslatedText.ShouldBe("Tylko LF");
        rows[3].TranslatedText.ShouldBe("Tylko CR");
        rows[4].TranslatedText.ShouldBe("Dwa razy");
        rows[5].TranslatedText.ShouldBe("Spacja ");
        rows[6].TranslatedText.ShouldBe("Linia pierwsza\\n");
        rows[7].TranslatedText.ShouldBe("Pusty angielski");
    }

    [Fact]
    public async Task Migrate_OnACleanedRow_ShouldKeepItsStatusAuthorApproverAndDates()
    {
        // Arrange: the text the translator meant does not change, so nothing else may.
        await PlantAsync(1, EnglishWithoutABreak, "Szkic\r\n");
        await PlantAsync(2, EnglishWithoutABreak, "Zatwierdzone\r\n", status: "Approved", approverId: ApproverId);

        // Act
        await _dbContext.Database.MigrateAsync(MigrationId<RemoveTrailingLineBreaksFromTranslatedText>());

        // Assert
        Dictionary<long, StoredRow> rows = await ReadRowsAsync();
        rows[1].ShouldBe(new StoredRow(1, "Szkic", "Draft", SubmitterId, null, CreatedAt, UpdatedAt));
        rows[2].ShouldBe(new StoredRow(2, "Zatwierdzone", "Approved", SubmitterId, ApproverId, CreatedAt, UpdatedAt));
    }

    [Fact]
    public async Task Migrate_OnRowsTheRuleDoesNotCover_ShouldLeaveThemAsTheyAre()
    {
        // Arrange
        await PlantAsync(1, "Line one\n", "Linia pierwsza\r\n");
        await PlantAsync(2, "Line one\r", "Linia pierwsza\n");
        await PlantAsync(3, EnglishWithoutABreak, "Pierwsza\r\nDruga");
        await PlantAsync(4, EnglishWithoutABreak, "Linia pierwsza\\n");
        await PlantAsync(5, EnglishWithoutABreak, "Spacja na końcu ");
        await PlantAsync(6, EnglishWithoutABreak, polish: null, status: "Untranslated");
        // Only whitespace: removing the breaks would leave an empty text the domain never stores.
        await PlantAsync(7, EnglishWithoutABreak, "\r\n");
        await PlantAsync(8, EnglishWithoutABreak, "  \n");

        // Act
        await _dbContext.Database.MigrateAsync(MigrationId<RemoveTrailingLineBreaksFromTranslatedText>());

        // Assert
        Dictionary<long, StoredRow> rows = await ReadRowsAsync();
        rows[1].TranslatedText.ShouldBe("Linia pierwsza\r\n");
        rows[2].TranslatedText.ShouldBe("Linia pierwsza\n");
        rows[3].TranslatedText.ShouldBe("Pierwsza\r\nDruga");
        rows[4].TranslatedText.ShouldBe("Linia pierwsza\\n");
        rows[5].TranslatedText.ShouldBe("Spacja na końcu ");
        rows[6].TranslatedText.ShouldBeNull();
        rows[7].TranslatedText.ShouldBe("\r\n");
        rows[8].TranslatedText.ShouldBe("  \n");
    }

    public RemoveTrailingLineBreaksMigrationTests(TranslationSystemApiFactory factory)
    {
        _factory = factory;
    }

    private static string MigrationId<TMigration>()
        where TMigration : Migration
        => typeof(TMigration).GetCustomAttribute<MigrationAttribute>()!.Id;

    private async Task<string> ExecuteOnSharedDatabaseAsync(string sql)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext sharedDbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
        await sharedDbContext.Database.ExecuteSqlRawAsync(sql);
        return sharedDbContext.Database.GetConnectionString()!;
    }

    private Task PlantAsync(
        long gossipId, string source, string? polish, string status = "Draft", Guid? approverId = null)
    {
        Guid? submitterId = polish is null ? null : SubmitterId;

        return _dbContext.Database.ExecuteSqlAsync($"""
            INSERT INTO translation."Translations"
                ("Id", "FileId", "GossipId", "SourceText", "TranslatedText", "Status", "SubmittedById",
                 "ApprovedById", "IntroducedInVersion", "CreatedAt", "UpdatedAt")
            VALUES ({Guid.CreateVersion7()}, {FileId}, {gossipId}, {source}, CAST({polish} AS text), {status},
                    CAST({submitterId} AS uuid), CAST({approverId} AS uuid), {VersionId}, {CreatedAt}, {UpdatedAt});
            """);
    }

    private async Task<Dictionary<long, StoredRow>> ReadRowsAsync()
    {
        List<StoredRow> rows = await _dbContext.Database.SqlQuery<StoredRow>($"""
            SELECT "GossipId", "TranslatedText", "Status", "SubmittedById", "ApprovedById", "CreatedAt", "UpdatedAt"
            FROM translation."Translations"
            """).ToListAsync();

        return rows.ToDictionary(row => row.GossipId);
    }

    private sealed record StoredRow(
        long GossipId,
        string? TranslatedText,
        string Status,
        Guid? SubmittedById,
        Guid? ApprovedById,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);
}
