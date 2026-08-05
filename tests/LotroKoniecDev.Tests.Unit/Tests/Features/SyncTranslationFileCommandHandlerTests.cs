using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Features.TranslationFileSyncing;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Primitives.Enums;
using Microsoft.Extensions.Logging.Abstractions;

namespace LotroKoniecDev.Tests.Unit.Tests.Features;

public sealed class SyncTranslationFileCommandHandlerTests
{
    private const string BaseUrl = "https://tms.example.com";
    private const string FilePath = "translations/polish.txt";

    private static readonly Uri Endpoint = new($"{BaseUrl}/api/v1/translation-files/pl");

    private readonly ITranslationFileEndpointResolver _endpointResolver =
        Substitute.For<ITranslationFileEndpointResolver>();

    private readonly ITranslationFileDownloader _downloader = Substitute.For<ITranslationFileDownloader>();
    private readonly ITranslationFileCache _cache = Substitute.For<ITranslationFileCache>();
    private readonly SyncTranslationFileCommandHandler _sut;

    public SyncTranslationFileCommandHandlerTests()
    {
        // The endpoint always comes from discovery (#611); resolution itself is covered by
        // TranslationFileEndpointResolverTests, so here it is a stubbed boundary like any other.
        _endpointResolver.ResolveAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(Endpoint));
        _cache.SaveEndpointHref(Arg.Any<string>(), Arg.Any<string>()).Returns(Result.Success());

        _sut = new SyncTranslationFileCommandHandler(
            _endpointResolver,
            _downloader,
            _cache,
            new SyncTranslationFileCommandValidator(),
            NullLogger<SyncTranslationFileCommandHandler>.Instance);
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
        await _downloader.DidNotReceive().FetchAsync(Arg.Any<Uri>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("http://tms.example.com")]
    [InlineData("http://192.168.1.10:5002")]
    [InlineData("HTTP://tms.example.com")]
    [InlineData("http://localhost.evil.com")]
    public async Task Handle_PlainHttpNonLocalhostUrl_ShouldReturnValidationErrorAndNotFetch(string baseUrl)
    {
        // Act — plain http hands the file to an on-path attacker (AUDIT-SEC-01), so it is rejected.
        Result<TranslationFileSyncResponse> result =
            await _sut.Handle(new SyncTranslationFileCommand(baseUrl, FilePath), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Type.ShouldBe(ErrorType.Validation);
        await _downloader.DidNotReceive().FetchAsync(Arg.Any<Uri>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("https://tms.example.com")]
    [InlineData("http://localhost:5002")]
    [InlineData("http://127.0.0.1:5002")]
    [InlineData("http://[::1]:5002")]
    public async Task Handle_HttpsOrLoopbackHttpUrl_ShouldPassValidationAndFetch(string baseUrl)
    {
        // Arrange — loopback has no network hop, so the localhost dev exception keeps plain http.
        _downloader.FetchAsync(Arg.Any<Uri>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(TranslationFileFetchResult.NotModified()));

        // Act
        Result<TranslationFileSyncResponse> result =
            await _sut.Handle(new SyncTranslationFileCommand(baseUrl, FilePath), CancellationToken.None);

        // Assert — reaching the UpToDate outcome proves validation passed and the fetch ran.
        result.IsSuccess.ShouldBeTrue();
        result.Value.Outcome.ShouldBe(TranslationFileSyncOutcome.UpToDate);
    }

    [Fact]
    public async Task Handle_WhenDownloadFailsTheIntegrityCheck_ShouldRejectItAndContinueWithLocalFile()
    {
        // Arrange — a tampered/corrupted download is rejected by the downloader (AUDIT-SEC-01); the
        // sync must fall back to the cached file and never save the rejected content.
        _downloader.FetchAsync(Endpoint, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<TranslationFileFetchResult>(
                DomainErrors.TranslationFileSync.IntegrityCheckFailed("hash mismatch")));

        // Act
        Result<TranslationFileSyncResponse> result = await _sut.Handle(Command(), CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Outcome.ShouldBe(TranslationFileSyncOutcome.IntegrityCheckFailedUsedCache);
        _cache.DidNotReceive().Save(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task Handle_WhenServerReturnsNewFile_ShouldSaveItAndReportUpdated()
    {
        // Arrange
        _downloader.FetchAsync(Endpoint, Arg.Any<string?>(), Arg.Any<CancellationToken>())
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
        _downloader.FetchAsync(Endpoint, Arg.Any<string?>(), Arg.Any<CancellationToken>())
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
        _downloader.FetchAsync(Endpoint, Arg.Any<string?>(), Arg.Any<CancellationToken>())
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
        _downloader.FetchAsync(Endpoint, Arg.Any<string?>(), Arg.Any<CancellationToken>())
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
        _downloader.FetchAsync(Endpoint, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(TranslationFileFetchResult.NotModified()));

        // Act
        Result<TranslationFileSyncResponse> result = await _sut.Handle(Command(), CancellationToken.None);

        // Assert — the conditional request carries the cached ETag, enabling the 304 path.
        result.IsSuccess.ShouldBeTrue();
        await _downloader.Received(1).FetchAsync(Endpoint, "\"cached-etag\"", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ShouldDownloadFromTheEndpointDiscoveryResolved()
    {
        // Arrange — the base URL is the only configured input; where the file lives comes from the
        // service document, and the cached href is offered to the resolver as its outage fallback.
        _cache.ReadEndpointHref(FilePath).Returns("https://tms.example.com/old/endpoint");
        _downloader.FetchAsync(Endpoint, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(TranslationFileFetchResult.NotModified()));

        // Act
        Result<TranslationFileSyncResponse> result = await _sut.Handle(Command(), CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        await _endpointResolver.Received(1).ResolveAsync(
            BaseUrl, "https://tms.example.com/old/endpoint", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenTheEndpointCannotBeResolved_ShouldNotDownloadAndContinueWithLocalFile()
    {
        // Arrange — nothing to fetch from: no path is ever guessed (#611). The launch still must not
        // be blocked on the network, so this reports instead of failing.
        _endpointResolver.ResolveAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<Uri>(
                DomainErrors.TranslationFileSync.EndpointDiscoveryUnavailable("the server is unreachable.")));

        // Act
        Result<TranslationFileSyncResponse> result = await _sut.Handle(Command(), CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Outcome.ShouldBe(TranslationFileSyncOutcome.EndpointUnresolvedUsedCache);
        result.Value.Detail.ShouldNotBeNull().ShouldContain("unreachable");
        await _downloader.DidNotReceive().FetchAsync(Arg.Any<Uri>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenTheEndpointServedTheFile_ShouldRememberItAsTheOutageFallback()
    {
        // Arrange — a 304 is proof enough that the endpoint works, so it becomes the last-known-good.
        _downloader.FetchAsync(Endpoint, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(TranslationFileFetchResult.NotModified()));

        // Act
        Result<TranslationFileSyncResponse> result = await _sut.Handle(Command(), CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        _cache.Received(1).SaveEndpointHref(FilePath, Endpoint.ToString());
    }

    [Fact]
    public async Task Handle_WhenTheResolvedEndpointIsUnchanged_ShouldNotRewriteTheSidecar()
    {
        // Arrange — the steady state writes nothing, so a read-only run keeps behaving as it did
        // before the endpoint sidecar existed.
        _cache.ReadEndpointHref(FilePath).Returns(Endpoint.ToString());
        _downloader.FetchAsync(Endpoint, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(TranslationFileFetchResult.NotModified()));

        // Act
        Result<TranslationFileSyncResponse> result = await _sut.Handle(Command(), CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        _cache.DidNotReceive().SaveEndpointHref(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task Handle_WhenTheEndpointSidecarCannotBeWritten_ShouldStillCompleteTheSync()
    {
        // Arrange — the sidecar is a hint for a future outage, not the payload: an unwritable
        // translations directory must not block the launch any more than an unreachable server does
        // (spec 0001 Q5). Before the endpoint sidecar existed a 304 wrote nothing at all, and that
        // install must keep launching.
        _downloader.FetchAsync(Endpoint, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(TranslationFileFetchResult.NotModified()));
        _cache.SaveEndpointHref(FilePath, Endpoint.ToString())
            .Returns(Result.Failure(DomainErrors.TranslationFileSync.CacheWriteError(FilePath, "disk full")));

        // Act
        Result<TranslationFileSyncResponse> result = await _sut.Handle(Command(), CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Outcome.ShouldBe(TranslationFileSyncOutcome.UpToDate);
    }

    [Fact]
    public async Task Handle_WhenTheDownloadedFileCannotBeWritten_ShouldReturnFailure()
    {
        // Arrange — the counterpart to the test above: here the downloaded payload itself is lost,
        // so silence would leave the user patching a file that is not the one the server served.
        _downloader.FetchAsync(Endpoint, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(TranslationFileFetchResult.Modified("content", "\"etag\"")));
        _cache.Save(FilePath, "content", "\"etag\"")
            .Returns(Result.Failure(DomainErrors.TranslationFileSync.CacheWriteError(FilePath, "disk full")));

        // Act
        Result<TranslationFileSyncResponse> result = await _sut.Handle(Command(), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("TranslationFileSync.CacheWriteError");
    }
}
