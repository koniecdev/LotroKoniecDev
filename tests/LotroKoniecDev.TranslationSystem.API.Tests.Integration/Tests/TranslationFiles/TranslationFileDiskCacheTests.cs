using System.Text;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using LotroKoniecDev.TranslationSystem.API.Features.TranslationFiles;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.ReadDbContexts;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.WriteDbContexts;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.TranslationFiles;

/// <summary>
/// The disk cache's own seam (PERF-09, #715, ADR-0064): the cases a single HTTP request cannot set up,
/// such as a hash that a rebuild replaced between the endpoint's hash lookup and the copy.
/// </summary>
[Collection("TranslationApi")]
public sealed class TranslationFileDiskCacheTests : IAsyncLifetime
{
    private const int FileId = 620756992;
    private const string Language = "pl";
    private const string AnyHash = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 0, 0, 0, TimeSpan.Zero);

    private readonly TranslationSystemApiFactory _factory;
    private GameVersionId _versionId;
    private TranslatorId _submitterId;

    public TranslationFileDiskCacheTests(TranslationSystemApiFactory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync(
            "TRUNCATE translation.\"Translations\", translation.\"GameVersions\", translation.\"TranslationArtifacts\", translation.\"Translators\" CASCADE;");

        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();

        GameVersion gameVersion = GameVersion.Create(LotroNotationVersion.Create("48.0").Value, Now).Value;
        dbContext.GameVersions.Add(gameVersion);

        Translator submitter = Translator.Create(
            IdentityId.Create(), DisplayName.Create("Seed Author").Value, email: null, Now).Value;
        dbContext.Translators.Add(submitter);

        await dbContext.SaveChangesAsync();
        _versionId = gameVersion.Id;
        _submitterId = submitter.Id;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task OpenAsync_WithAHashARebuildReplaced_ShouldReturnTheCurrentCopy()
    {
        // Arrange: the caller read the hash, then a rebuild replaced the file before the copy was opened.
        await SeedApprovedAsync(gossipId: 1, polish: "Alfa");
        await RebuildAsync();
        string replacedHash = await CurrentHashAsync();
        await SeedApprovedAsync(gossipId: 2, polish: "Beta");
        await RebuildAsync();
        string currentHash = await CurrentHashAsync();

        // Act
        TranslationFileCopy? copy = await DiskCache().OpenAsync(Language, replacedHash, CancellationToken.None);

        // Assert: the newer copy comes back under its own hash, so the endpoint tags the body it sends.
        copy.ShouldNotBeNull();
        await using Stream content = copy.Content;
        copy.ContentHash.ShouldBe(currentHash);
        string body = await new StreamReader(content, Encoding.UTF8).ReadToEndAsync();
        body.ShouldContain($"{FileId}||2||Beta||NULL||NULL||1");
    }

    [Fact]
    public async Task OpenAsync_WhenNoFileWasBuilt_ShouldReturnNull()
    {
        // Act
        TranslationFileCopy? copy = await DiskCache().OpenAsync(Language, AnyHash, CancellationToken.None);

        // Assert
        copy.ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("../../etc/passwd")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDE")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0")]
    [InlineData("G123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF012345678/ABCDEF")]
    public async Task OpenAsync_WithSomethingOtherThanAHexSha256_ShouldThrow(string contentHash)
    {
        // Act + Assert: the hash becomes a file name, so it can never carry a path.
        await Should.ThrowAsync<ArgumentException>(
            () => DiskCache().OpenAsync(Language, contentHash, CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../pl")]
    [InlineData("p/l")]
    public async Task OpenAsync_WithALanguageThatIsNotLettersOnly_ShouldThrow(string language)
    {
        // Act + Assert
        await Should.ThrowAsync<ArgumentException>(
            () => DiskCache().OpenAsync(language, AnyHash, CancellationToken.None));
    }

    private ITranslationFileDiskCache DiskCache()
        => _factory.Services.GetRequiredService<ITranslationFileDiskCache>();

    private async Task<string> CurrentHashAsync()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        IApplicationReadDbContext readDbContext = scope.ServiceProvider.GetRequiredService<IApplicationReadDbContext>();
        return await readDbContext.PrecomputedTranslationFiles
            .Where(file => file.Language == Language)
            .Select(file => file.ContentHash)
            .SingleAsync();
    }

    private async Task SeedApprovedAsync(int gossipId, string polish)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();

        Translation row = Translation.CreateUntranslated(
            FragmentKey.Create(FileId, gossipId).Value,
            TranslationSource.Create("English", null, null).Value,
            _versionId,
            Now).Value;
        row.ProvideTranslation(polish, _submitterId, Now);
        row.Approve(_submitterId, Now);

        dbContext.Translations.Add(row);
        await dbContext.SaveChangesAsync();
    }

    private async Task RebuildAsync()
    {
        IPrecomputedTranslationFileProjector projector = _factory.Services.GetRequiredService<IPrecomputedTranslationFileProjector>();
        await projector.RebuildAsync(Language, CancellationToken.None);
    }
}
