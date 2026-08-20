using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.WriteDbContexts;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.GameVersions;

/// <summary>
/// Pins the database backstop behind the DeleteGameVersion guard (AUDIT-EF-05). The three version
/// pointer columns on Translations have Restrict foreign keys to GameVersions, so even a delete that
/// goes around <c>AnyReferencesGameVersionAsync</c> cannot leave rows pointing at nothing. That happens
/// in the gap between the check and the delete, for example when an import stamps the version in
/// between.
/// It uses the DbContext directly on purpose; the endpoint path is covered by
/// <see cref="DeleteGameVersionTests"/>.
/// </summary>
[Collection("TranslationApi")]
public sealed class GameVersionPointerForeignKeyTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 7, 10, 0, 0, 0, TimeSpan.Zero);

    private readonly TranslationSystemApiFactory _factory;

    public GameVersionPointerForeignKeyTests(TranslationSystemApiFactory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync(
            "TRUNCATE translation.\"Translations\", translation.\"GameVersions\" CASCADE;");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public enum VersionPointer
    {
        Introduced,
        LastSourceChange,
        Removed,
    }

    [Theory]
    [InlineData(VersionPointer.Introduced)]
    [InlineData(VersionPointer.LastSourceChange)]
    [InlineData(VersionPointer.Removed)]
    public async Task DeleteGameVersion_RowStillReferencedByAPointerColumn_ShouldBeRejectedByTheForeignKey(
        VersionPointer pointer)
    {
        // Arrange: a translation whose given pointer references the version under deletion (the
        // other pointers reference a different version, so each FK is exercised in isolation).
        GameVersionId baseVersionId = await SeedVersionAsync("47.0");
        GameVersionId referencedVersionId = await SeedVersionAsync("48.0");
        await SeedTranslationWithPointerAsync(pointer, baseVersionId, referencedVersionId);

        // Act
        DbUpdateException thrown = await Should.ThrowAsync<DbUpdateException>(
            () => DeleteVersionAsync(referencedVersionId));

        // Assert: a PostgreSQL foreign-key violation (23503) blocked the delete and the row survived.
        PostgresException postgresException = thrown.InnerException.ShouldBeOfType<PostgresException>();
        postgresException.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
        (await VersionExistsAsync(referencedVersionId)).ShouldBeTrue();
    }

    private async Task<GameVersionId> SeedVersionAsync(string version)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();

        GameVersion gameVersion = GameVersion.Create(LotroNotationVersion.Create(version).Value, Now).Value;
        dbContext.GameVersions.Add(gameVersion);
        await dbContext.SaveChangesAsync();

        return gameVersion.Id;
    }

    private async Task SeedTranslationWithPointerAsync(
        VersionPointer pointer,
        GameVersionId baseVersionId,
        GameVersionId referencedVersionId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();

        Translation row = Translation.CreateUntranslated(
            FragmentKey.Create(620756992, 1001).Value,
            TranslationSource.Create("Witaj", null, null).Value,
            pointer is VersionPointer.Introduced ? referencedVersionId : baseVersionId,
            Now).Value;

        switch (pointer)
        {
            case VersionPointer.LastSourceChange:
                row.ApplySourceChange(TranslationSource.Create("Witaj ponownie", null, null).Value, referencedVersionId, Now);
                break;
            case VersionPointer.Removed:
                row.MarkRemoved(referencedVersionId, Now);
                break;
        }

        dbContext.Translations.Add(row);
        await dbContext.SaveChangesAsync();
    }

    private async Task DeleteVersionAsync(GameVersionId versionId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();

        GameVersion gameVersion = await dbContext.GameVersions.SingleAsync(version => version.Id == versionId);
        dbContext.GameVersions.Remove(gameVersion);
        await dbContext.SaveChangesAsync();
    }

    private async Task<bool> VersionExistsAsync(GameVersionId versionId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();

        return await dbContext.GameVersions.AnyAsync(version => version.Id == versionId);
    }
}
