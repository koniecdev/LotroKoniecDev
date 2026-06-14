using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Features.TranslationFileSyncing;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Primitives.Enums;

namespace LotroKoniecDev.Tests.Unit.Tests.Features;

public sealed class SyncTranslationFileCommandHandlerTests
{
    private const string BaseUrl = "https://tms.example.com";
    private const string FilePath = "translations/polish.txt";

    private readonly ITranslationFileDownloader _downloader = Substitute.For<ITranslationFileDownloader>();
    private readonly ITranslationFileCache _cache = Substitute.For<ITranslationFileCache>();
    private readonly SyncTranslationFileCommandHandler _sut;

    public SyncTranslationFileCommandHandlerTests()
    {
        _sut = new SyncTranslationFileCommandHandler(_downloader, _cache, new SyncTranslationFileCommandValidator());
    }

    private static SyncTranslationFileCommand Command() => new(BaseUrl, FilePath);

    [Fact]
    public async Task Handle_NullCommand_ShouldThrow()
    {
        await Should.ThrowAsync<ArgumentNullException>(
            () => _sut.Handle(null!, CancellationToken.None).AsTask());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-url")]
    [InlineData("ftp://tms.example.com")]
    public async Task Handle_InvalidBaseUrl_ShouldReturnValidationErrorAndNotFetch(string baseUrl)
    {
        // Act
        Result<TranslationFileSyncResponse> result =
            await _sut.Handle(new SyncTranslationFileCommand(baseUrl, FilePath), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Type.ShouldBe(ErrorType.Validation);
        await _downloader.DidNotReceive().FetchAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenServerReturnsNewFile_ShouldSaveItAndReportUpdated()
    {
        // Arrange
        _downloader.FetchAsync(BaseUrl, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(TranslationFileFetchResult.Modified("polish content", "\"etag-1\"")));
        _cache.Save(FilePath, "polish content", "\"etag-1\"").Returns(Result.Success());

        // Act
        Result<TranslationFileSyncResponse> result = await _sut.Handle(Command(), CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Outcome.ShouldBe(TranslationFileSyncOutcome.Updated);
    }

    [Fact]
    public async Task Handle_WhenServerReturnsNotModified_ShouldKeepCacheAndNotSave()
    {
        // Arrange
        _downloader.FetchAsync(BaseUrl, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(TranslationFileFetchResult.NotModified()));

        // Act
        Result<TranslationFileSyncResponse> result = await _sut.Handle(Command(), CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Outcome.ShouldBe(TranslationFileSyncOutcome.UpToDate);
        _cache.DidNotReceive().Save(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task Handle_WhenServerUnreachable_ShouldNotBlockLaunchAndContinueWithLocalFile()
    {
        // Arrange — the launch must never be blocked on the network (spec 0001 Q5). Whether a local
        // translation file exists is the launch path's concern, so the sync only ever warns here.
        _downloader.FetchAsync(BaseUrl, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<TranslationFileFetchResult>(DomainErrors.TranslationFileSync.NetworkError("offline")));

        // Act
        Result<TranslationFileSyncResponse> result = await _sut.Handle(Command(), CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Outcome.ShouldBe(TranslationFileSyncOutcome.OfflineUsedCache);
        _cache.DidNotReceive().Save(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task Handle_WhenSaveFails_ShouldReturnFailure()
    {
        // Arrange
        _downloader.FetchAsync(BaseUrl, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(TranslationFileFetchResult.Modified("content", "\"etag\"")));
        _cache.Save(FilePath, "content", "\"etag\"")
            .Returns(Result.Failure(DomainErrors.TranslationFileSync.CacheWriteError(FilePath, "disk full")));

        // Act
        Result<TranslationFileSyncResponse> result = await _sut.Handle(Command(), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("TranslationFileSync.CacheWriteError");
    }

    [Fact]
    public async Task Handle_ShouldSendTheCachedETagOnTheConditionalRequest()
    {
        // Arrange
        _cache.ReadETag(FilePath).Returns("\"cached-etag\"");
        _downloader.FetchAsync(BaseUrl, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(TranslationFileFetchResult.NotModified()));

        // Act
        Result<TranslationFileSyncResponse> result = await _sut.Handle(Command(), CancellationToken.None);

        // Assert — the conditional request carries the cached ETag, enabling the 304 path.
        result.IsSuccess.ShouldBeTrue();
        await _downloader.Received(1).FetchAsync(BaseUrl, "\"cached-etag\"", Arg.Any<CancellationToken>());
    }
}
