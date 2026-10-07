using System.Text;
using LotroKoniecDev.Application.Features.TranslationFileSyncing;
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
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

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

    public Task DisposeAsync()
    {
        _factory.ReadContextSqlRecorder.BeforeCommand = null;
        return Task.CompletedTask;
    }

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

    [Fact]
    public async Task OpenAsync_WhenTheStoredHashDoesNotMatchTheContent_ShouldThrowAndKeepNoFile()
    {
        // Arrange: the row's hash no longer describes its content, so the patcher would refuse the body.
        await SeedApprovedAsync(gossipId: 1, polish: "Alfa");
        await RebuildAsync();
        await OverwriteStoredHashAsync(AnyHash);

        // Act + Assert: a loud failure, and no file under a name that promises the wrong bytes.
        await Should.ThrowAsync<InvalidOperationException>(
            () => DiskCache().OpenAsync(Language, AnyHash, CancellationToken.None));
        Directory.GetFiles(_factory.TranslationFileCopiesDirectory).ShouldBeEmpty();
    }

    [Fact]
    public async Task OpenAsync_WhenTheCallerGaveUpBeforeTheWrite_ShouldThrowAndLeaveTheGateUsable()
    {
        // Arrange
        await SeedApprovedAsync(gossipId: 1, polish: "Alfa");
        await RebuildAsync();
        string hash = await CurrentHashAsync();
        _factory.ReadContextSqlRecorder.Clear();

        // Act
        Exception? cancelled = await Record.ExceptionAsync(
            () => DiskCache().OpenAsync(Language, hash, new CancellationToken(canceled: true)));
        TranslationFileCopy? copy = await DiskCache().OpenAsync(Language, hash, CancellationToken.None);

        // Assert: the caller that gave up loaded nothing, and the next one still gets its copy.
        cancelled.ShouldBeAssignableTo<OperationCanceledException>();
        copy.ShouldNotBeNull();
        await using Stream content = copy.Content;
        copy.ContentHash.ShouldBe(hash);
        _factory.ReadContextSqlRecorder.Commands
            .Count(command => command.Contains("\"Content\""))
            .ShouldBe(1);
    }

    [Fact]
    public async Task OpenAsync_WhenTheCallerCancelsDuringTheWrite_ShouldStillFinishTheCopy()
    {
        // Arrange: the caller gives up at the moment the content starts loading, as a CLI does after its
        // 10 seconds on a slow first write.
        await SeedApprovedAsync(gossipId: 1, polish: "Alfa");
        await RebuildAsync();
        string hash = await CurrentHashAsync();
        using CancellationTokenSource caller = new();
        _factory.ReadContextSqlRecorder.BeforeCommand = command =>
        {
            if (command.Contains("\"Content\""))
            {
                caller.Cancel();
            }
        };

        // Act
        TranslationFileCopy? copy = await DiskCache().OpenAsync(Language, hash, caller.Token);

        // Assert: the write runs on the host's token, so the next waiter finds the copy instead of
        // loading the whole file again.
        caller.IsCancellationRequested.ShouldBeTrue();
        copy.ShouldNotBeNull();
        await using Stream content = copy.Content;
        copy.ContentHash.ShouldBe(hash);
        File.Exists(Path.Combine(_factory.TranslationFileCopiesDirectory, $"{Language}-{hash}.txt")).ShouldBeTrue();
    }

    [Fact]
    public async Task OpenAsync_AfterACrashLeftFilesBehind_ShouldKeepOnlyTheNewCopy()
    {
        // Arrange: an older copy and a half-written temporary file from a process that died.
        await SeedApprovedAsync(gossipId: 1, polish: "Alfa");
        await RebuildAsync();
        string hash = await CurrentHashAsync();
        Directory.CreateDirectory(_factory.TranslationFileCopiesDirectory);
        await File.WriteAllTextAsync(Path.Combine(_factory.TranslationFileCopiesDirectory, $"{Language}-{AnyHash}.txt"), "old");
        await File.WriteAllTextAsync(Path.Combine(_factory.TranslationFileCopiesDirectory, $"{Language}-{Guid.NewGuid():N}.tmp"), "half");

        // Act
        TranslationFileCopy? copy = await DiskCache().OpenAsync(Language, hash, CancellationToken.None);

        // Assert
        copy.ShouldNotBeNull();
        await using Stream content = copy.Content;
        Directory.GetFiles(_factory.TranslationFileCopiesDirectory)
            .Select(Path.GetFileName)
            .ShouldBe([$"{Language}-{hash}.txt"]);
    }

    [Fact]
    public async Task OpenAsync_WithNoDirectoryConfigured_ShouldUseAPrivateFolderPerProcessAndRemoveItOnDispose()
    {
        // Arrange: the deployed setup. A fixed folder in a shared temp folder could be created first by
        // another local user, who could then place a file there under a valid ETag.
        await SeedApprovedAsync(gossipId: 1, polish: "Alfa");
        await RebuildAsync();
        string hash = await CurrentHashAsync();
        using TranslationFileDiskCache first = CreateCacheWithoutConfiguredDirectory();
        using TranslationFileDiskCache second = CreateCacheWithoutConfiguredDirectory();

        // Act
        string firstFolder;
        string secondFolder;
        await using (Stream firstContent = (await first.OpenAsync(Language, hash, CancellationToken.None))!.Content)
        await using (Stream secondContent = (await second.OpenAsync(Language, hash, CancellationToken.None))!.Content)
        {
            firstFolder = Path.GetDirectoryName(((FileStream)firstContent).Name)!;
            secondFolder = Path.GetDirectoryName(((FileStream)secondContent).Name)!;
        }

        // Windows keeps the temp folder per user, so the owner-only mode exists on Linux and macOS only.
        bool ownerOnly = OperatingSystem.IsWindows()
                         || File.GetUnixFileMode(firstFolder) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        first.Dispose();
        second.Dispose();

        // Assert
        Path.GetFileName(firstFolder).ShouldStartWith("lotro-translation-files-");
        ownerOnly.ShouldBeTrue();
        firstFolder.ShouldNotBe(secondFolder);
        Directory.Exists(firstFolder).ShouldBeFalse();
        Directory.Exists(secondFolder).ShouldBeFalse();
    }

    [Fact]
    public async Task OpenAsync_WhenTheStoredHashIsLowerCase_ShouldServeTheCopyLikeThePatcherWouldAcceptIt()
    {
        // Arrange: the patcher compares the hash without regard to case, so the copy must too.
        await SeedApprovedAsync(gossipId: 1, polish: "Alfa");
        await RebuildAsync();
        string lowerCaseHash = (await CurrentHashAsync()).ToLowerInvariant();
        await OverwriteStoredHashAsync(lowerCaseHash);

        // Act
        TranslationFileCopy? copy = await DiskCache().OpenAsync(Language, lowerCaseHash, CancellationToken.None);

        // Assert
        copy.ShouldNotBeNull();
        await using Stream content = copy.Content;
        copy.ContentHash.ShouldBe(lowerCaseHash);
        string body = await new StreamReader(content, Encoding.UTF8).ReadToEndAsync();
        TranslationFileContentIntegrity.Matches(body, $"\"{lowerCaseHash}\"").ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("../../etc/passwd")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDE")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0")]
    [InlineData("G123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF012345678/ABCDEF")]
    public async Task OpenAsync_WithSomethingOtherThanAHexSha256_ShouldThrowAsAFaultInOurOwnData(string contentHash)
    {
        // Act + Assert: the hash becomes a file name, so it can never carry a path. It always comes
        // from the stored file, so a bad one is our fault and must not turn into the client's 400.
        await Should.ThrowAsync<InvalidOperationException>(
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

    private TranslationFileDiskCache CreateCacheWithoutConfiguredDirectory()
        => new(
            _factory.Services.GetRequiredService<IServiceScopeFactory>(),
            _factory.Services.GetRequiredService<IHostApplicationLifetime>(),
            Microsoft.Extensions.Options.Options.Create(new TranslationFileDiskCacheSettings()),
            NullLogger<TranslationFileDiskCache>.Instance);

    private async Task<string> CurrentHashAsync()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        IApplicationReadDbContext readDbContext = scope.ServiceProvider.GetRequiredService<IApplicationReadDbContext>();
        return await readDbContext.PrecomputedTranslationFiles
            .Where(file => file.Language == Language)
            .Select(file => file.ContentHash)
            .SingleAsync();
    }

    private async Task OverwriteStoredHashAsync(string contentHash)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
        await dbContext.Database.ExecuteSqlAsync(
            $"UPDATE translation.\"TranslationArtifacts\" SET \"ContentHash\" = {contentHash}");
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
