using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Features.AppUpdating;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;
using Microsoft.Extensions.Logging.Abstractions;

namespace LotroKoniecDev.Tests.Unit.Tests.Features;

public sealed class CheckForAppUpdateQueryHandlerTests
{
    private const string Installed = "0.2.0";

    private readonly IGitHubReleaseClient _releaseClient = Substitute.For<IGitHubReleaseClient>();
    private readonly IAppUpdateWorkspace _workspace = Substitute.For<IAppUpdateWorkspace>();
    private readonly CheckForAppUpdateQueryHandler _sut;

    [Fact]
    public async Task Handle_NullQuery_Throws()
    {
        await Should.ThrowAsync<ArgumentNullException>(() => _sut.Handle(null!, CancellationToken.None).AsTask());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0.0.0")]
    [InlineData("1.0")]
    [InlineData("not a version")]
    public async Task Handle_DevelopmentBuildOrNoStamp_AsksNothing(string? installed)
    {
        // Act
        Result<AppUpdateCheckResponse> result = await _sut.Handle(new CheckForAppUpdateQuery(installed), CancellationToken.None);

        // Assert
        result.Value.Outcome.ShouldBe(AppUpdateCheckOutcome.DevelopmentBuild);
        await _releaseClient.DidNotReceive().ListReleasesAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("0.0.0")]
    [InlineData(Installed)]
    public async Task Handle_AnyBuild_CleansUpOldDownloadsFirst(string installed)
    {
        // Arrange
        ReturnsReleases();

        // Act
        await _sut.Handle(new CheckForAppUpdateQuery(installed), CancellationToken.None);

        // Assert: the clean-up has no return value to show it ran.
        _workspace.Received(1).CleanUpOldDownloads();
    }

    [Fact]
    public async Task Handle_ANewerDesktopRelease_OffersItWithGitHubsReleaseAddresses()
    {
        // Arrange
        ReturnsReleases(Release("desktop-v0.3.0"));

        // Act
        Result<AppUpdateCheckResponse> result = await Check();

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Outcome.ShouldBe(AppUpdateCheckOutcome.UpdateAvailable);
        result.Value.Update.ShouldBe(new AvailableAppUpdate(
            new DesktopVersion(0, 3, 0),
            new Uri("https://github.com/koniecdev/LotroKoniecDev/releases/download/desktop-v0.3.0/LotroPoPolsku-Setup-0.3.0.exe"),
            new Uri("https://github.com/koniecdev/LotroKoniecDev/releases/download/desktop-v0.3.0/LotroPoPolsku-Setup-0.3.0.exe.sha256")));
    }

    [Fact]
    public async Task Handle_SeveralReleases_OffersTheHighestVersionWhateverTheOrder()
    {
        // Arrange: GitHub lists by date, so a later fix of an older line can come first.
        ReturnsReleases(Release("desktop-v0.3.1"), Release("v0.9.0"), Release("desktop-v0.10.0"), Release("desktop-v0.4.0"));

        // Act
        Result<AppUpdateCheckResponse> result = await Check();

        // Assert
        result.Value.Update.ShouldNotBeNull().Version.ShouldBe(new DesktopVersion(0, 10, 0));
    }

    [Theory]
    [InlineData("desktop-v0.2.0")]
    [InlineData("desktop-v0.1.9")]
    public async Task Handle_NoReleaseNewerThanTheApp_IsUpToDate(string tag)
    {
        // Arrange
        ReturnsReleases(Release(tag));

        // Act
        Result<AppUpdateCheckResponse> result = await Check();

        // Assert
        result.Value.Outcome.ShouldBe(AppUpdateCheckOutcome.UpToDate);
        result.Value.Update.ShouldBeNull();
    }

    public static TheoryData<GitHubRelease> ReleasesThatAreNeverOffered() =>
    [
        Release("desktop-v9.0.0") with { IsDraft = true },
        Release("desktop-v9.0.0") with { IsPrerelease = true },
        Release("v9.0.0"),
        Release("desktop-v09.0.0"),
        Release("desktop-v9.0.0-rc1"),
        Release("desktop-v9.0.0") with { Assets = [Asset("desktop-v9.0.0", "LotroPoPolsku-Setup-9.0.0.exe")] },
        Release("desktop-v9.0.0") with { Assets = [Asset("desktop-v9.0.0", "LotroPoPolsku-Setup-9.0.0.exe.sha256")] },
        Release("desktop-v9.0.0") with
        {
            Assets =
            [
                Asset("desktop-v9.0.0", "LotroPoPolsku-Setup-9.0.1.exe"),
                Asset("desktop-v9.0.0", "LotroPoPolsku-Setup-9.0.1.exe.sha256")
            ]
        },
        Release("desktop-v9.0.0") with
        {
            Assets =
            [
                new GitHubReleaseAsset("LotroPoPolsku-Setup-9.0.0.exe", new Uri("https://evil.example.com/LotroPoPolsku-Setup-9.0.0.exe")),
                Asset("desktop-v9.0.0", "LotroPoPolsku-Setup-9.0.0.exe.sha256")
            ]
        },
        Release("desktop-v9.0.0") with
        {
            Assets =
            [
                new GitHubReleaseAsset(
                    "LotroPoPolsku-Setup-9.0.0.exe",
                    new Uri("https://github.com/someone/else/releases/download/desktop-v9.0.0/LotroPoPolsku-Setup-9.0.0.exe")),
                Asset("desktop-v9.0.0", "LotroPoPolsku-Setup-9.0.0.exe.sha256")
            ]
        },
        Release("desktop-v9.0.0") with
        {
            Assets =
            [
                new GitHubReleaseAsset("LotroPoPolsku-Setup-9.0.0.exe", null),
                Asset("desktop-v9.0.0", "LotroPoPolsku-Setup-9.0.0.exe.sha256")
            ]
        }
    ];

    [Theory]
    [MemberData(nameof(ReleasesThatAreNeverOffered))]
    public async Task Handle_AReleaseOffTheContract_IsNeverOfferedAndTheNextOneIs(GitHubRelease bad)
    {
        // Arrange
        ReturnsReleases(bad, Release("desktop-v0.3.0"));

        // Act
        Result<AppUpdateCheckResponse> result = await Check();

        // Assert
        result.Value.Update.ShouldNotBeNull().Version.ShouldBe(new DesktopVersion(0, 3, 0));
    }

    [Fact]
    public async Task Handle_NoDesktopReleaseAmongTheListed_IsAFailedCheckNotUpToDate()
    {
        // Arrange: only server releases and a draft.
        ReturnsReleases(Release("v0.1.0"), Release("desktop-v0.3.0") with { IsDraft = true });

        // Act
        Result<AppUpdateCheckResponse> result = await Check();

        // Assert
        result.Value.Outcome.ShouldBe(AppUpdateCheckOutcome.CheckFailed);
        result.Value.Failure.ShouldNotBeNull().Code.ShouldBe(DomainErrors.AppUpdate.NoDesktopReleaseCode);
    }

    [Fact]
    public async Task Handle_AnEmptyList_IsAFailedCheck()
    {
        // Arrange
        ReturnsReleases();

        // Act
        Result<AppUpdateCheckResponse> result = await Check();

        // Assert
        result.Value.Outcome.ShouldBe(AppUpdateCheckOutcome.CheckFailed);
        result.Value.Failure.ShouldNotBeNull().Code.ShouldBe(DomainErrors.AppUpdate.NoDesktopReleaseCode);
    }

    [Theory]
    [InlineData(DomainErrors.AppUpdate.NetworkErrorCode)]
    [InlineData(DomainErrors.AppUpdate.TimedOutCode)]
    [InlineData(DomainErrors.AppUpdate.RateLimitedCode)]
    [InlineData(DomainErrors.AppUpdate.UnexpectedStatusCode)]
    [InlineData(DomainErrors.AppUpdate.HostRefusedCode)]
    [InlineData(DomainErrors.AppUpdate.ResponseTooLargeCode)]
    [InlineData(DomainErrors.AppUpdate.InvalidResponseCode)]
    public async Task Handle_TheListCannotBeRead_IsAFailedCheckWithTheReason(string code)
    {
        // Arrange
        _releaseClient.ListReleasesAsync(Arg.Any<CancellationToken>())
            .Returns(Result.Failure<IReadOnlyList<GitHubRelease>>(new Domain.Core.BuildingBlocks.Error(code, "reason")));

        // Act
        Result<AppUpdateCheckResponse> result = await Check();

        // Assert: the check never fails the caller, so the app keeps running.
        result.IsSuccess.ShouldBeTrue();
        result.Value.Outcome.ShouldBe(AppUpdateCheckOutcome.CheckFailed);
        result.Value.Failure.ShouldNotBeNull().Code.ShouldBe(code);
    }

    public CheckForAppUpdateQueryHandlerTests()
    {
        _sut = new CheckForAppUpdateQueryHandler(
            _releaseClient, _workspace, NullLogger<CheckForAppUpdateQueryHandler>.Instance);
    }

    private static GitHubRelease Release(string tag)
    {
        string version = tag.StartsWith("desktop-v", StringComparison.Ordinal) ? tag["desktop-v".Length..] : tag;
        return new GitHubRelease(
            tag,
            IsDraft: false,
            IsPrerelease: false,
            [Asset(tag, $"LotroPoPolsku-Setup-{version}.exe"), Asset(tag, $"LotroPoPolsku-Setup-{version}.exe.sha256")]);
    }

    private static GitHubReleaseAsset Asset(string tag, string name) =>
        new(name, new Uri($"https://github.com/koniecdev/LotroKoniecDev/releases/download/{tag}/{name}"));

    private void ReturnsReleases(params GitHubRelease[] releases) =>
        _releaseClient.ListReleasesAsync(Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<GitHubRelease>>(releases));

    private Task<Result<AppUpdateCheckResponse>> Check() =>
        _sut.Handle(new CheckForAppUpdateQuery(Installed), CancellationToken.None).AsTask();
}
