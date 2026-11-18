using System.Net;
using System.Text;
using LotroKoniecDev.Application;
using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Features.AppUpdating;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Infrastructure.Network;
using LotroKoniecDev.Tests.Infrastructure.Shared;

namespace LotroKoniecDev.Tests.Infrastructure.Tests;

/// <summary>
/// The GitHub client over an in-memory stub handler, with no network (spec 0016). The stub sets the
/// answer's <see cref="HttpResponseMessage.RequestMessage"/> the way the real handler does after
/// redirects: that address is what the client checks.
/// </summary>
public sealed class GitHubReleaseClientTests : IDisposable
{
    private const string ReleasesJson =
        """
        [
          {
            "tag_name": "desktop-v0.3.0",
            "draft": false,
            "prerelease": false,
            "body": "Notatki",
            "assets": [
              {
                "name": "LotroPoPolsku-Setup-0.3.0.exe",
                "browser_download_url": "https://github.com/koniecdev/LotroKoniecDev/releases/download/desktop-v0.3.0/LotroPoPolsku-Setup-0.3.0.exe"
              },
              { "name": "broken", "browser_download_url": "not a url" }
            ]
          },
          { "tag_name": "v0.1.0", "draft": true, "prerelease": true, "assets": [] },
          { "tag_name": null, "draft": false, "prerelease": false, "assets": null }
        ]
        """;

    private static readonly Uri ListUri = new("https://api.github.com/repos/koniecdev/LotroKoniecDev/releases?per_page=100");

    private static readonly Uri InstallerUri =
        new("https://github.com/koniecdev/LotroKoniecDev/releases/download/desktop-v0.3.0/LotroPoPolsku-Setup-0.3.0.exe");

    private static readonly Uri AssetHostUri = new("https://release-assets.githubusercontent.com/github-production-release-asset/1");

    /// <summary>Well under the stubs' own waits, so a missing limit fails the test instead of passing.</summary>
    private static readonly TimeSpan ShortLimit = TimeSpan.FromMilliseconds(250);

    /// <summary>The other limit in a test, longer than any stub waits, so a swapped limit fails it too.</summary>
    private static readonly TimeSpan LongLimit = TimeSpan.FromSeconds(30);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"lotro-update-client-tests-{Guid.NewGuid():N}");
    private readonly IProgress<OperationProgress> _progress = Substitute.For<IProgress<OperationProgress>>();

    private string TargetPath => Path.Combine(_directory, "LotroPoPolsku-Setup-0.3.0.exe");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task ListReleasesAsync_GitHubsAnswer_ReadsTagsFlagsAndAssets()
    {
        // Arrange
        using HttpResponseMessage response = Answer(HttpStatusCode.OK, Json(ReleasesJson), ListUri);
        GitHubReleaseClient sut = Create(response, out _);

        // Act
        Result<IReadOnlyList<GitHubRelease>> result = await sut.ListReleasesAsync(CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Count.ShouldBe(3);
        result.Value[0].TagName.ShouldBe("desktop-v0.3.0");
        result.Value[0].IsDraft.ShouldBeFalse();
        result.Value[0].Assets[0].ShouldBe(new GitHubReleaseAsset("LotroPoPolsku-Setup-0.3.0.exe", InstallerUri));
        result.Value[0].Assets[1].DownloadUri.ShouldBeNull();
        result.Value[1].IsDraft.ShouldBeTrue();
        result.Value[1].IsPrerelease.ShouldBeTrue();
        result.Value[2].TagName.ShouldBe(string.Empty);
        result.Value[2].Assets.ShouldBeEmpty();
    }

    [Fact]
    public async Task ListReleasesAsync_AnyCall_AsksTheListAddressAsGitHubJsonWithNoPinnedApiVersion()
    {
        // Arrange
        using HttpResponseMessage response = Answer(HttpStatusCode.OK, Json("[]"), ListUri);
        GitHubReleaseClient sut = Create(response, out StubHttpMessageHandler handler);

        // Act
        await sut.ListReleasesAsync(CancellationToken.None);

        // Assert: GitHub answers 410 Gone to a retired API version, and a shipped app would never
        // update again.
        HttpRequestMessage request = handler.LastRequest.ShouldNotBeNull();
        request.RequestUri.ShouldBe(ListUri);
        request.Headers.Accept.ToString().ShouldBe("application/vnd.github+json");
        request.Headers.Contains("X-GitHub-Api-Version").ShouldBeFalse();
    }

    [Theory]
    [InlineData("https://evil.example.com/releases")]
    [InlineData("http://api.github.com/repos/koniecdev/LotroKoniecDev/releases")]
    public async Task ListReleasesAsync_AnAnswerFromOutsideGitHub_IsRefused(string answeredFrom)
    {
        // Arrange
        using HttpResponseMessage response = Answer(HttpStatusCode.OK, Json(ReleasesJson), new Uri(answeredFrom));
        GitHubReleaseClient sut = Create(response, out _);

        // Act
        Result<IReadOnlyList<GitHubRelease>> result = await sut.ListReleasesAsync(CancellationToken.None);

        // Assert
        result.Error.Code.ShouldBe(DomainErrors.AppUpdate.HostRefusedCode);
    }

    [Fact]
    public async Task ListReleasesAsync_AnAnswerWithNoAddress_IsRefused()
    {
        // Arrange: the real handler always sets it, so a missing one is never trusted.
        using HttpResponseMessage response = new(HttpStatusCode.OK) { Content = Json(ReleasesJson) };
        GitHubReleaseClient sut = Create(response, out _);

        // Act
        Result<IReadOnlyList<GitHubRelease>> result = await sut.ListReleasesAsync(CancellationToken.None);

        // Assert
        result.Error.Code.ShouldBe(DomainErrors.AppUpdate.HostRefusedCode);
    }

    [Fact]
    public async Task ListReleasesAsync_ARedirectThatCameBackUnfollowed_IsRefused()
    {
        // Arrange: .NET hands back an https to http redirect instead of following it.
        using HttpResponseMessage response = Answer(HttpStatusCode.Found, Json(string.Empty), ListUri);
        response.Headers.Location = new Uri("http://api.github.com/repos/koniecdev/LotroKoniecDev/releases");
        GitHubReleaseClient sut = Create(response, out _);

        // Act
        Result<IReadOnlyList<GitHubRelease>> result = await sut.ListReleasesAsync(CancellationToken.None);

        // Assert
        result.Error.Code.ShouldBe(DomainErrors.AppUpdate.HostRefusedCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task ListReleasesAsync_GitHubsRateLimit_IsReportedAsSuch(HttpStatusCode status)
    {
        // Arrange
        using HttpResponseMessage response = Answer(status, Json("{}"), ListUri);
        GitHubReleaseClient sut = Create(response, out _);

        // Act
        Result<IReadOnlyList<GitHubRelease>> result = await sut.ListReleasesAsync(CancellationToken.None);

        // Assert
        result.Error.Code.ShouldBe(DomainErrors.AppUpdate.RateLimitedCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task ListReleasesAsync_AnyOtherFailureStatus_IsAnUnexpectedStatus(HttpStatusCode status)
    {
        // Arrange: GitHub was reached, so the player must not read "no connection".
        using HttpResponseMessage response = Answer(status, Json("{}"), ListUri);
        GitHubReleaseClient sut = Create(response, out _);

        // Act
        Result<IReadOnlyList<GitHubRelease>> result = await sut.ListReleasesAsync(CancellationToken.None);

        // Assert
        result.Error.Code.ShouldBe(DomainErrors.AppUpdate.UnexpectedStatusCode);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"message\":\"an object, not a list\"}")]
    [InlineData("null")]
    [InlineData("[{\"tag_name\": 5}]")]
    [InlineData("[null]")]
    [InlineData("[{\"tag_name\":\"desktop-v1.0.0\",\"draft\":false,\"prerelease\":false,\"assets\":[null]}]")]
    public async Task ListReleasesAsync_AnAnswerThatIsNotAReleaseList_IsAnInvalidResponse(string body)
    {
        // Arrange
        using HttpResponseMessage response = Answer(HttpStatusCode.OK, Json(body), ListUri);
        GitHubReleaseClient sut = Create(response, out _);

        // Act
        Result<IReadOnlyList<GitHubRelease>> result = await sut.ListReleasesAsync(CancellationToken.None);

        // Assert
        result.Error.Code.ShouldBe(DomainErrors.AppUpdate.InvalidResponseCode);
    }

    [Fact]
    public async Task ListReleasesAsync_AListOverItsCap_IsRefusedWhileStreaming()
    {
        // Arrange
        using HttpResponseMessage response = Answer(
            HttpStatusCode.OK, new UndeclaredLengthContent(32 * 1024 * 1024 + 1), ListUri);
        GitHubReleaseClient sut = Create(response, out _);

        // Act
        Result<IReadOnlyList<GitHubRelease>> result = await sut.ListReleasesAsync(CancellationToken.None);

        // Assert
        result.Error.Code.ShouldBe(DomainErrors.AppUpdate.ResponseTooLargeCode);
    }

    [Fact]
    public async Task ListReleasesAsync_ACancelledWait_IsATimeout()
    {
        // Arrange
        using HttpResponseMessage response = Answer(HttpStatusCode.OK, new StallingContent(), ListUri);
        GitHubReleaseClient sut = Create(response, out _);
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        // Act
        Result<IReadOnlyList<GitHubRelease>> result = await sut.ListReleasesAsync(cancelled.Token);

        // Assert
        result.Error.Code.ShouldBe(DomainErrors.AppUpdate.TimedOutCode);
    }

    [Fact]
    public async Task ListReleasesAsync_ABodyThatStallsPastTheCheckLimit_IsATimeout()
    {
        // Arrange: the headers arrive and the body never does. HttpClient.Timeout has ended with the
        // headers, so only the check's own limit ends the wait.
        using HttpResponseMessage response = Answer(HttpStatusCode.OK, new StallingContent(), ListUri);
        GitHubReleaseClient sut = CreateWithLimits(response, checkTimeLimit: ShortLimit, downloadTimeLimit: LongLimit);

        // Act
        Result<IReadOnlyList<GitHubRelease>> result = await sut.ListReleasesAsync(CancellationToken.None);

        // Assert
        result.Error.Code.ShouldBe(DomainErrors.AppUpdate.TimedOutCode);
    }

    [Fact]
    public async Task ListReleasesAsync_NoNetwork_IsANetworkError()
    {
        // Arrange: the most common failure at start.
        GitHubReleaseClient sut = new(new HttpClient(new ThrowingHttpMessageHandler()));

        // Act
        Result<IReadOnlyList<GitHubRelease>> result = await sut.ListReleasesAsync(CancellationToken.None);

        // Assert
        result.Error.Code.ShouldBe(DomainErrors.AppUpdate.NetworkErrorCode);
    }

    [Fact]
    public async Task ListReleasesAsync_ABodyThatBreaksOffHalfWay_IsANetworkError()
    {
        // Arrange
        using HttpResponseMessage response = Answer(HttpStatusCode.OK, new BreakingContent(1000), ListUri);
        GitHubReleaseClient sut = Create(response, out _);

        // Act
        Result<IReadOnlyList<GitHubRelease>> result = await sut.ListReleasesAsync(CancellationToken.None);

        // Assert
        result.Error.Code.ShouldBe(DomainErrors.AppUpdate.NetworkErrorCode);
    }

    [Fact]
    public async Task DownloadTextAsync_GitHubsAnswer_ReturnsTheText()
    {
        // Arrange
        using HttpResponseMessage response = Answer(HttpStatusCode.OK, new StringContent("hash  name\n"), AssetHostUri);
        GitHubReleaseClient sut = Create(response, out _);

        // Act
        Result<string> result = await sut.DownloadTextAsync(InstallerUri, 1024, CancellationToken.None);

        // Assert
        result.Value.ShouldBe("hash  name\n");
    }

    [Fact]
    public async Task DownloadTextAsync_AnAddressOutsideGitHub_IsRefusedWithoutARequest()
    {
        // Arrange
        using HttpResponseMessage response = Answer(HttpStatusCode.OK, new StringContent("x"), AssetHostUri);
        GitHubReleaseClient sut = Create(response, out StubHttpMessageHandler handler);

        // Act
        Result<string> result = await sut.DownloadTextAsync(new Uri("https://evil.example.com/x"), 1024, CancellationToken.None);

        // Assert
        result.Error.Code.ShouldBe(DomainErrors.AppUpdate.HostRefusedCode);
        handler.LastRequest.ShouldBeNull();
    }

    [Fact]
    public async Task DownloadTextAsync_ATextOverItsLimit_IsRefused()
    {
        // Arrange
        using HttpResponseMessage response = Answer(HttpStatusCode.OK, new UndeclaredLengthContent(1025), AssetHostUri);
        GitHubReleaseClient sut = Create(response, out _);

        // Act
        Result<string> result = await sut.DownloadTextAsync(InstallerUri, 1024, CancellationToken.None);

        // Assert
        result.Error.Code.ShouldBe(DomainErrors.AppUpdate.ResponseTooLargeCode);
    }

    [Fact]
    public async Task DownloadTextAsync_NoNetwork_IsANetworkError()
    {
        // Arrange
        GitHubReleaseClient sut = new(new HttpClient(new ThrowingHttpMessageHandler()));

        // Act
        Result<string> result = await sut.DownloadTextAsync(InstallerUri, 1024, CancellationToken.None);

        // Assert
        result.Error.Code.ShouldBe(DomainErrors.AppUpdate.NetworkErrorCode);
    }

    [Fact]
    public async Task DownloadTextAsync_ABodyThatBreaksOffHalfWay_IsANetworkError()
    {
        // Arrange
        using HttpResponseMessage response = Answer(HttpStatusCode.OK, new BreakingContent(100), AssetHostUri);
        GitHubReleaseClient sut = Create(response, out _);

        // Act
        Result<string> result = await sut.DownloadTextAsync(InstallerUri, 1024, CancellationToken.None);

        // Assert
        result.Error.Code.ShouldBe(DomainErrors.AppUpdate.NetworkErrorCode);
    }

    [Fact]
    public async Task DownloadTextAsync_ACancelledWait_IsATimeout()
    {
        // Arrange
        using HttpResponseMessage response = Answer(HttpStatusCode.OK, new StallingContent(), AssetHostUri);
        GitHubReleaseClient sut = Create(response, out _);
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        // Act
        Result<string> result = await sut.DownloadTextAsync(InstallerUri, 1024, cancelled.Token);

        // Assert
        result.Error.Code.ShouldBe(DomainErrors.AppUpdate.TimedOutCode);
    }

    [Fact]
    public async Task DownloadTextAsync_ABodyThatStallsPastTheCheckLimit_IsATimeout()
    {
        // Arrange
        using HttpResponseMessage response = Answer(HttpStatusCode.OK, new StallingContent(), AssetHostUri);
        GitHubReleaseClient sut = CreateWithLimits(response, checkTimeLimit: ShortLimit, downloadTimeLimit: LongLimit);

        // Act
        Result<string> result = await sut.DownloadTextAsync(InstallerUri, 1024, CancellationToken.None);

        // Assert
        result.Error.Code.ShouldBe(DomainErrors.AppUpdate.TimedOutCode);
    }

    [Fact]
    public async Task DownloadTextAsync_AnAnswerFromOutsideGitHub_IsRefused()
    {
        // Arrange
        using HttpResponseMessage response = Answer(HttpStatusCode.OK, new StringContent("x"), new Uri("https://evil.example.com/x"));
        GitHubReleaseClient sut = Create(response, out _);

        // Act
        Result<string> result = await sut.DownloadTextAsync(InstallerUri, 1024, CancellationToken.None);

        // Assert
        result.Error.Code.ShouldBe(DomainErrors.AppUpdate.HostRefusedCode);
    }

    [Fact]
    public async Task DownloadFileAsync_GitHubsAnswer_WritesTheBytesAndReportsTheEnd()
    {
        // Arrange
        Directory.CreateDirectory(_directory);
        byte[] body = Encoding.UTF8.GetBytes("installer bytes");
        using HttpResponseMessage response = Answer(HttpStatusCode.OK, new ByteArrayContent(body), AssetHostUri);
        GitHubReleaseClient sut = Create(response, out _);

        // Act
        Result result = await sut.DownloadFileAsync(InstallerUri, TargetPath, 1024, _progress, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        (await File.ReadAllBytesAsync(TargetPath)).ShouldBe(body);
        _progress.Received().Report(Arg.Is<OperationProgress>(progress => progress.IsCompleted));
    }

    [Fact]
    public async Task DownloadFileAsync_ABodyInSeveralPieces_ReportsItsProgressInKibAndTheEnd()
    {
        // Arrange: 300 KiB with a declared length, read in pieces of 80 KiB.
        Directory.CreateDirectory(_directory);
        byte[] body = new byte[300 * 1024];
        using HttpResponseMessage response = Answer(HttpStatusCode.OK, new ByteArrayContent(body), AssetHostUri);
        GitHubReleaseClient sut = Create(response, out _);

        // Act
        Result result = await sut.DownloadFileAsync(InstallerUri, TargetPath, body.Length, _progress, CancellationToken.None);

        // Assert: the shared progress bar is the only trace of the progress.
        result.IsSuccess.ShouldBeTrue();
        _progress.Received(1).Report(new OperationProgress(80, 300));
        _progress.Received(1).Report(new OperationProgress(1, 1));
    }

    [Theory]
    [InlineData("write")]
    [InlineData("flush")]
    [InlineData("close")]
    public async Task DownloadFileAsync_TheDiskFillsUp_IsAFileSystemErrorAndLeavesNoFile(string failingStep)
    {
        // Arrange: a failed write or flush fails again when the file is closed, as a file stream does.
        Directory.CreateDirectory(_directory);
        using HttpResponseMessage response = Answer(
            HttpStatusCode.OK, new ByteArrayContent(Encoding.UTF8.GetBytes("installer bytes")), AssetHostUri);
        GitHubReleaseClient sut = new(
            new HttpClient(new StubHttpMessageHandler(response)),
            LongLimit,
            LongLimit,
            path =>
            {
                File.WriteAllBytes(path, []);
                return new FailingTargetStream(
                    failWrite: failingStep == "write", failFlush: failingStep == "flush", failClose: true);
            });

        // Act
        Result result = await sut.DownloadFileAsync(InstallerUri, TargetPath, 1024, _progress, CancellationToken.None);

        // Assert: a full disk must not reach the player as "no connection".
        result.Error.Code.ShouldBe(DomainErrors.AppUpdate.FileSystemErrorCode);
        File.Exists(TargetPath).ShouldBeFalse();
    }

    [Fact]
    public async Task DownloadFileAsync_TheWaitEndsBeforeTheBodyArrives_LeavesNoTargetOpen()
    {
        // Arrange: Windows cannot delete a file that is still open, so an open target would stay on disk.
        Directory.CreateDirectory(_directory);
        using HttpResponseMessage response = Answer(HttpStatusCode.OK, new StallingContent(), AssetHostUri);
        List<FailingTargetStream> opened = [];
        GitHubReleaseClient sut = new(
            new HttpClient(new StubHttpMessageHandler(response)),
            LongLimit,
            ShortLimit,
            _ =>
            {
                FailingTargetStream target = new(failWrite: false, failFlush: false, failClose: false);
                opened.Add(target);
                return target;
            });

        // Act
        Result result = await sut.DownloadFileAsync(InstallerUri, TargetPath, 1024, _progress, CancellationToken.None);

        // Assert
        result.Error.Code.ShouldBe(DomainErrors.AppUpdate.TimedOutCode);
        opened.ShouldAllBe(target => target.IsClosed);
    }

    [Fact]
    public async Task DownloadFileAsync_TheCopyStallsAndTheCloseFailsToo_IsStillATimeout()
    {
        // Arrange: the stall decides the answer; a close that fails after it must not hide it.
        Directory.CreateDirectory(_directory);
        using HttpResponseMessage response = Answer(
            HttpStatusCode.OK, new SlowContent(Encoding.UTF8.GetBytes("installer bytes"), TimeSpan.FromSeconds(8)), AssetHostUri);
        GitHubReleaseClient sut = new(
            new HttpClient(new StubHttpMessageHandler(response)),
            LongLimit,
            ShortLimit,
            _ => new FailingTargetStream(failWrite: false, failFlush: false, failClose: true));

        // Act
        Result result = await sut.DownloadFileAsync(InstallerUri, TargetPath, 1024, _progress, CancellationToken.None);

        // Assert
        result.Error.Code.ShouldBe(DomainErrors.AppUpdate.TimedOutCode);
    }

    [Fact]
    public async Task DownloadFileAsync_ABodyThatStallsInsideTheCopy_IsATimeoutAndLeavesNoFile()
    {
        // Arrange: a line that dies without a reset stalls in the middle of the body, where the copy
        // reads. It must not hold the update, and the gate with it, until the app restarts.
        Directory.CreateDirectory(_directory);
        using HttpResponseMessage response = Answer(
            HttpStatusCode.OK, new SlowContent(Encoding.UTF8.GetBytes("installer bytes"), TimeSpan.FromSeconds(8)), AssetHostUri);
        GitHubReleaseClient sut = CreateWithLimits(response, checkTimeLimit: LongLimit, downloadTimeLimit: ShortLimit);

        // Act
        Result result = await sut.DownloadFileAsync(InstallerUri, TargetPath, 1024, _progress, CancellationToken.None);

        // Assert
        result.Error.Code.ShouldBe(DomainErrors.AppUpdate.TimedOutCode);
        File.Exists(TargetPath).ShouldBeFalse();
    }

    [Fact]
    public async Task DownloadFileAsync_ABodySlowerThanTheCheckLimit_StillDownloads()
    {
        // Arrange: the installer has its own, longer limit, never the check's.
        Directory.CreateDirectory(_directory);
        byte[] body = Encoding.UTF8.GetBytes("installer bytes");
        using HttpResponseMessage response = Answer(
            HttpStatusCode.OK, new SlowContent(body, TimeSpan.FromSeconds(1)), AssetHostUri);
        GitHubReleaseClient sut = CreateWithLimits(response, checkTimeLimit: ShortLimit, downloadTimeLimit: LongLimit);

        // Act
        Result result = await sut.DownloadFileAsync(InstallerUri, TargetPath, 1024, _progress, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        (await File.ReadAllBytesAsync(TargetPath)).ShouldBe(body);
    }

    [Fact]
    public async Task DownloadFileAsync_ABodySlowerThanTheClientTimeout_StillDownloads()
    {
        // Arrange: the shared client gives a request 10 s, the installer download 10 minutes. The
        // client's limit must end with the headers, or every download over a slow line would fail.
        Directory.CreateDirectory(_directory);
        byte[] body = Encoding.UTF8.GetBytes("installer bytes");
        using HttpResponseMessage response = Answer(
            HttpStatusCode.OK, new SlowContent(body, TimeSpan.FromSeconds(2)), AssetHostUri);
        GitHubReleaseClient sut = new(
            new HttpClient(new StubHttpMessageHandler(response)) { Timeout = TimeSpan.FromSeconds(1) });

        // Act
        Result result = await sut.DownloadFileAsync(InstallerUri, TargetPath, 1024, _progress, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        (await File.ReadAllBytesAsync(TargetPath)).ShouldBe(body);
    }

    [Fact]
    public async Task DownloadFileAsync_ABodyOverTheLimit_IsRefusedAndLeavesNoFile()
    {
        // Arrange: no declared length, so only the counting while streaming can stop it.
        Directory.CreateDirectory(_directory);
        using HttpResponseMessage response = Answer(HttpStatusCode.OK, new UndeclaredLengthContent(200_000), AssetHostUri);
        GitHubReleaseClient sut = Create(response, out _);

        // Act
        Result result = await sut.DownloadFileAsync(InstallerUri, TargetPath, 100_000, _progress, CancellationToken.None);

        // Assert
        result.Error.Code.ShouldBe(DomainErrors.AppUpdate.ResponseTooLargeCode);
        File.Exists(TargetPath).ShouldBeFalse();
    }

    [Fact]
    public async Task DownloadFileAsync_ADeclaredLengthOverTheLimit_IsRefusedBeforeAnyWrite()
    {
        // Arrange
        Directory.CreateDirectory(_directory);
        using HttpResponseMessage response = Answer(HttpStatusCode.OK, new ByteArrayContent(new byte[2048]), AssetHostUri);
        GitHubReleaseClient sut = Create(response, out _);

        // Act
        Result result = await sut.DownloadFileAsync(InstallerUri, TargetPath, 1024, _progress, CancellationToken.None);

        // Assert
        result.Error.Code.ShouldBe(DomainErrors.AppUpdate.ResponseTooLargeCode);
        File.Exists(TargetPath).ShouldBeFalse();
    }

    [Fact]
    public async Task DownloadFileAsync_AnAnswerFromOutsideGitHub_IsRefusedAndLeavesNoFile()
    {
        // Arrange
        Directory.CreateDirectory(_directory);
        using HttpResponseMessage response = Answer(
            HttpStatusCode.OK, new ByteArrayContent([1, 2, 3]), new Uri("https://evil.example.com/installer.exe"));
        GitHubReleaseClient sut = Create(response, out _);

        // Act
        Result result = await sut.DownloadFileAsync(InstallerUri, TargetPath, 1024, _progress, CancellationToken.None);

        // Assert
        result.Error.Code.ShouldBe(DomainErrors.AppUpdate.HostRefusedCode);
        File.Exists(TargetPath).ShouldBeFalse();
    }

    [Fact]
    public async Task DownloadFileAsync_AFileThatIsAlreadyThere_IsNeverWrittenOverOrDeleted()
    {
        // Arrange
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(TargetPath, "someone else's file");
        using HttpResponseMessage response = Answer(HttpStatusCode.OK, new ByteArrayContent([1, 2, 3]), AssetHostUri);
        GitHubReleaseClient sut = Create(response, out _);

        // Act
        Result result = await sut.DownloadFileAsync(InstallerUri, TargetPath, 1024, _progress, CancellationToken.None);

        // Assert
        result.Error.Code.ShouldBe(DomainErrors.AppUpdate.FileSystemErrorCode);
        (await File.ReadAllTextAsync(TargetPath)).ShouldBe("someone else's file");
    }

    [Fact]
    public async Task DownloadFileAsync_AFileThatAppearsAfterTheCheck_IsNeverDeleted()
    {
        // Arrange: another program creates the file between the check and the open, so the open fails.
        Directory.CreateDirectory(_directory);
        using HttpResponseMessage response = Answer(HttpStatusCode.OK, new ByteArrayContent([1, 2, 3]), AssetHostUri);
        GitHubReleaseClient sut = new(
            new HttpClient(new StubHttpMessageHandler(response)),
            DesktopReleasePolicy.CheckTimeLimit,
            DesktopReleasePolicy.DownloadTimeLimit,
            path =>
            {
                File.WriteAllText(path, "someone else's file");
                throw new IOException("The file already exists.");
            });

        // Act
        Result result = await sut.DownloadFileAsync(InstallerUri, TargetPath, 1024, _progress, CancellationToken.None);

        // Assert
        result.Error.Code.ShouldBe(DomainErrors.AppUpdate.FileSystemErrorCode);
        (await File.ReadAllTextAsync(TargetPath)).ShouldBe("someone else's file");
    }

    [Fact]
    public async Task DownloadFileAsync_AnAddressOutsideGitHub_IsRefusedWithoutARequest()
    {
        // Arrange
        Directory.CreateDirectory(_directory);
        using HttpResponseMessage response = Answer(HttpStatusCode.OK, new ByteArrayContent([1, 2, 3]), AssetHostUri);
        GitHubReleaseClient sut = Create(response, out StubHttpMessageHandler handler);

        // Act
        Result result = await sut.DownloadFileAsync(
            new Uri("https://evil.example.com/installer.exe"), TargetPath, 1024, _progress, CancellationToken.None);

        // Assert
        result.Error.Code.ShouldBe(DomainErrors.AppUpdate.HostRefusedCode);
        handler.LastRequest.ShouldBeNull();
        File.Exists(TargetPath).ShouldBeFalse();
    }

    [Fact]
    public async Task DownloadFileAsync_NoNetwork_IsANetworkErrorAndLeavesNoFile()
    {
        // Arrange
        Directory.CreateDirectory(_directory);
        GitHubReleaseClient sut = new(new HttpClient(new ThrowingHttpMessageHandler()));

        // Act
        Result result = await sut.DownloadFileAsync(InstallerUri, TargetPath, 1024, _progress, CancellationToken.None);

        // Assert
        result.Error.Code.ShouldBe(DomainErrors.AppUpdate.NetworkErrorCode);
        File.Exists(TargetPath).ShouldBeFalse();
    }

    [Fact]
    public async Task DownloadFileAsync_ABodyThatBreaksOffHalfWay_IsANetworkErrorAndLeavesNoPartialFile()
    {
        // Arrange
        Directory.CreateDirectory(_directory);
        using HttpResponseMessage response = Answer(HttpStatusCode.OK, new BreakingContent(200_000), AssetHostUri);
        GitHubReleaseClient sut = Create(response, out _);

        // Act
        Result result = await sut.DownloadFileAsync(InstallerUri, TargetPath, 1_000_000, _progress, CancellationToken.None);

        // Assert
        result.Error.Code.ShouldBe(DomainErrors.AppUpdate.NetworkErrorCode);
        File.Exists(TargetPath).ShouldBeFalse();
    }

    [Fact]
    public async Task DownloadFileAsync_ACancelledWait_IsATimeoutAndLeavesNoFile()
    {
        // Arrange
        Directory.CreateDirectory(_directory);
        using HttpResponseMessage response = Answer(HttpStatusCode.OK, new StallingContent(), AssetHostUri);
        GitHubReleaseClient sut = Create(response, out _);
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        // Act
        Result result = await sut.DownloadFileAsync(InstallerUri, TargetPath, 1024, _progress, cancelled.Token);

        // Assert
        result.Error.Code.ShouldBe(DomainErrors.AppUpdate.TimedOutCode);
        File.Exists(TargetPath).ShouldBeFalse();
    }

    [Fact]
    public async Task DownloadFileAsync_ATargetThatCannotBeCreated_IsAFileSystemError()
    {
        // Arrange: the target's folder does not exist.
        using HttpResponseMessage response = Answer(HttpStatusCode.OK, new ByteArrayContent([1, 2, 3]), AssetHostUri);
        GitHubReleaseClient sut = Create(response, out _);

        // Act
        Result result = await sut.DownloadFileAsync(
            InstallerUri, Path.Combine(_directory, "missing", "installer.exe"), 1024, _progress, CancellationToken.None);

        // Assert
        result.Error.Code.ShouldBe(DomainErrors.AppUpdate.FileSystemErrorCode);
    }

    private static GitHubReleaseClient CreateWithLimits(
        HttpResponseMessage response, TimeSpan checkTimeLimit, TimeSpan downloadTimeLimit) =>
        new(new HttpClient(new StubHttpMessageHandler(response)), checkTimeLimit, downloadTimeLimit);

    private static GitHubReleaseClient Create(HttpResponseMessage response, out StubHttpMessageHandler handler)
    {
        handler = new StubHttpMessageHandler(response);
        return new GitHubReleaseClient(new HttpClient(handler));
    }

    private static HttpResponseMessage Answer(HttpStatusCode status, HttpContent content, Uri answeredFrom) =>
        new(status)
        {
            Content = content,
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, answeredFrom)
        };

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");
}
