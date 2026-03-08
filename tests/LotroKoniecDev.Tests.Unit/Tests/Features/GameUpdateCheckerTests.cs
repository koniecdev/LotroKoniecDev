using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Features.UpdateChecking;
using LotroKoniecDev.Domain.Core.BuildingBlocks;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Domain.Models;
using LotroKoniecDev.Primitives.Enums;
using Microsoft.Extensions.Logging;

namespace LotroKoniecDev.Tests.Unit.Tests.Features;

public sealed class GameUpdateCheckerTests
{
    private const string VersionFilePath = @"C:\temp\version.txt";

    private readonly IForumPageFetcher _mockFetcher;
    private readonly IGameVersionFileStore _mockStore;
    private readonly ILogger<GameUpdateChecker> _mockLogger;
    private readonly GameUpdateChecker _checker;

    private static readonly StoredVersionInfo StoredInfo401 = new("40.1", 100, 200);

    public GameUpdateCheckerTests()
    {
        _mockFetcher = Substitute.For<IForumPageFetcher>();
        _mockStore = Substitute.For<IGameVersionFileStore>();
        _mockLogger = Substitute.For<ILogger<GameUpdateChecker>>();
        _checker = new GameUpdateChecker(_mockFetcher, _mockStore, _mockLogger);
    }

    // ───────────────────────────── CheckForUpdateAsync — normal flow ─────────────────────────────

    [Fact]
    public async Task CheckForUpdateAsync_ShouldReturnForumVersionChanged_WhenNewVersionDetected()
    {
        // Arrange
        _mockFetcher.FetchReleaseNotesPageAsync()
            .Returns(Result.Success(ForumPageWithVersion("40.2")));
        _mockStore.ReadStoredVersion(VersionFilePath)
            .Returns(Result.Success<StoredVersionInfo?>(StoredInfo401));

        // Act
        Result<GameUpdateCheckSummary> result = await _checker.CheckForUpdateAsync(VersionFilePath);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ForumVersionChanged.ShouldBeTrue();
        result.Value.ForumVersion.ShouldBe("40.2");
        result.Value.StoredInfo.ShouldBe(StoredInfo401);
    }

    [Fact]
    public async Task CheckForUpdateAsync_ShouldNotSaveVersion_WhenNewVersionDetected()
    {
        // Arrange
        _mockFetcher.FetchReleaseNotesPageAsync()
            .Returns(Result.Success(ForumPageWithVersion("40.2")));
        _mockStore.ReadStoredVersion(VersionFilePath)
            .Returns(Result.Success<StoredVersionInfo?>(StoredInfo401));

        // Act
        await _checker.CheckForUpdateAsync(VersionFilePath);

        // Assert — CheckForUpdateAsync only reports, never saves
        _mockStore.DidNotReceive().SaveVersion(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>());
    }

    [Fact]
    public async Task CheckForUpdateAsync_ShouldReturnNoForumChange_WhenSameVersion()
    {
        // Arrange
        _mockFetcher.FetchReleaseNotesPageAsync()
            .Returns(Result.Success(ForumPageWithVersion("40.1")));
        _mockStore.ReadStoredVersion(VersionFilePath)
            .Returns(Result.Success<StoredVersionInfo?>(StoredInfo401));

        // Act
        Result<GameUpdateCheckSummary> result = await _checker.CheckForUpdateAsync(VersionFilePath);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ForumVersionChanged.ShouldBeFalse();
        result.Value.ForumVersion.ShouldBe("40.1");
        result.Value.StoredInfo.ShouldBe(StoredInfo401);
    }

    [Fact]
    public async Task CheckForUpdateAsync_ShouldNotSaveVersion_WhenSameVersion()
    {
        // Arrange
        _mockFetcher.FetchReleaseNotesPageAsync()
            .Returns(Result.Success(ForumPageWithVersion("40.1")));
        _mockStore.ReadStoredVersion(VersionFilePath)
            .Returns(Result.Success<StoredVersionInfo?>(StoredInfo401));

        // Act
        await _checker.CheckForUpdateAsync(VersionFilePath);

        // Assert
        _mockStore.DidNotReceive().SaveVersion(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>());
    }

    [Fact]
    public async Task CheckForUpdateAsync_ShouldReturnFailure_WhenVersionFileReadError()
    {
        // Arrange
        Error readError = new("GameUpdateCheck.VersionFileError", "Access denied", ErrorType.IoError);
        _mockStore.ReadStoredVersion(VersionFilePath)
            .Returns(Result.Failure<StoredVersionInfo?>(readError));

        // Act
        Result<GameUpdateCheckSummary> result = await _checker.CheckForUpdateAsync(VersionFilePath);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("GameUpdateCheck.VersionFileError");
    }

    // ───────────────────────────── CheckForUpdateAsync — graceful degradation ─────────────────────────────

    [Fact]
    public async Task CheckForUpdateAsync_ShouldReturnNoForumVersion_WhenForumFetchFails()
    {
        // Arrange
        _mockStore.ReadStoredVersion(VersionFilePath)
            .Returns(Result.Success<StoredVersionInfo?>(StoredInfo401));
        Error networkError = new("GameUpdateCheck.NetworkError", "Connection refused", ErrorType.IoError);
        _mockFetcher.FetchReleaseNotesPageAsync()
            .Returns(Result.Failure<string>(networkError));

        // Act
        Result<GameUpdateCheckSummary> result = await _checker.CheckForUpdateAsync(VersionFilePath);

        // Assert — forum fail is graceful, never blocks gaming
        result.IsSuccess.ShouldBeTrue();
        result.Value.ForumVersion.ShouldBeNull();
        result.Value.StoredInfo.ShouldBe(StoredInfo401);
        result.Value.ForumCheckSucceeded.ShouldBeFalse();
        result.Value.ForumVersionChanged.ShouldBeFalse();
    }

    [Fact]
    public async Task CheckForUpdateAsync_ShouldReturnNoForumVersion_WhenVersionNotFoundInPage()
    {
        // Arrange
        _mockFetcher.FetchReleaseNotesPageAsync()
            .Returns(Result.Success("<html><body>No version here</body></html>"));
        _mockStore.ReadStoredVersion(VersionFilePath)
            .Returns(Result.Success<StoredVersionInfo?>(StoredInfo401));

        // Act
        Result<GameUpdateCheckSummary> result = await _checker.CheckForUpdateAsync(VersionFilePath);

        // Assert — page format change is graceful, never blocks gaming
        result.IsSuccess.ShouldBeTrue();
        result.Value.ForumVersion.ShouldBeNull();
        result.Value.StoredInfo.ShouldBe(StoredInfo401);
    }

    // ───────────────────────────── CheckForUpdateAsync — first run ─────────────────────────────

    [Fact]
    public async Task CheckForUpdateAsync_ShouldReturnFirstLaunch_WhenNoStoredVersion()
    {
        // Arrange — no version.txt yet, forum works
        _mockStore.ReadStoredVersion(VersionFilePath)
            .Returns(Result.Success<StoredVersionInfo?>(null));
        _mockFetcher.FetchReleaseNotesPageAsync()
            .Returns(Result.Success(ForumPageWithVersion("40.2")));

        // Act
        Result<GameUpdateCheckSummary> result = await _checker.CheckForUpdateAsync(VersionFilePath);

        // Assert — first run, handler will establish baseline
        result.IsSuccess.ShouldBeTrue();
        result.Value.ForumVersion.ShouldBe("40.2");
        result.Value.StoredInfo.ShouldBeNull();
        result.Value.IsFirstLaunch.ShouldBeTrue();
    }

    [Fact]
    public async Task CheckForUpdateAsync_ShouldReturnFirstLaunchNoForum_WhenFirstRunAndForumFails()
    {
        // Arrange — no version.txt, AND forum unreachable
        _mockStore.ReadStoredVersion(VersionFilePath)
            .Returns(Result.Success<StoredVersionInfo?>(null));
        Error networkError = new("GameUpdateCheck.NetworkError", "Connection refused", ErrorType.IoError);
        _mockFetcher.FetchReleaseNotesPageAsync()
            .Returns(Result.Failure<string>(networkError));

        // Act
        Result<GameUpdateCheckSummary> result = await _checker.CheckForUpdateAsync(VersionFilePath);

        // Assert — zero info, graceful: just let the user play
        result.IsSuccess.ShouldBeTrue();
        result.Value.ForumVersion.ShouldBeNull();
        result.Value.StoredInfo.ShouldBeNull();
        result.Value.IsFirstLaunch.ShouldBeTrue();
        result.Value.ForumCheckSucceeded.ShouldBeFalse();
    }

    [Fact]
    public async Task CheckForUpdateAsync_ShouldReturnFirstLaunchNoForum_WhenFirstRunAndVersionNotFoundInPage()
    {
        // Arrange — no version.txt, forum reachable but page format changed
        _mockStore.ReadStoredVersion(VersionFilePath)
            .Returns(Result.Success<StoredVersionInfo?>(null));
        _mockFetcher.FetchReleaseNotesPageAsync()
            .Returns(Result.Success("<html><body>No version here</body></html>"));

        // Act
        Result<GameUpdateCheckSummary> result = await _checker.CheckForUpdateAsync(VersionFilePath);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ForumVersion.ShouldBeNull();
        result.Value.StoredInfo.ShouldBeNull();
    }

    // ───────────────────────────── CheckForUpdateAsync — input validation ─────────────────────────────

    [Fact]
    public async Task CheckForUpdateAsync_ShouldThrow_WhenNullPath()
    {
        // Act
        Func<Task> act = () => _checker.CheckForUpdateAsync(null!);

        // Assert
        await act.ShouldThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task CheckForUpdateAsync_ShouldThrow_WhenEmptyPath()
    {
        // Act
        Func<Task> act = () => _checker.CheckForUpdateAsync("");

        // Assert
        await act.ShouldThrowAsync<ArgumentException>();
    }

    // ───────────────────────────── CheckForUpdateAsync — parsing ─────────────────────────────

    [Fact]
    public async Task CheckForUpdateAsync_ShouldPickFirstVersion_WhenMultipleVersionsInHtml()
    {
        // Arrange — forum lists newest first, checker should pick "40.2"
        string html = """
            <a>Update 40.2 Release Notes</a>
            <a>Update 40.1 Release Notes</a>
            <a>Update 39.3 Release Notes</a>
            """;

        _mockFetcher.FetchReleaseNotesPageAsync()
            .Returns(Result.Success(html));
        _mockStore.ReadStoredVersion(VersionFilePath)
            .Returns(Result.Success<StoredVersionInfo?>(StoredInfo401));

        // Act
        Result<GameUpdateCheckSummary> result = await _checker.CheckForUpdateAsync(VersionFilePath);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ForumVersion.ShouldBe("40.2");
    }

    [Fact]
    public async Task CheckForUpdateAsync_ShouldMatch_WhenCaseInsensitiveHtml()
    {
        // Arrange
        _mockFetcher.FetchReleaseNotesPageAsync()
            .Returns(Result.Success("<a>UPDATE 40.2 RELEASE NOTES</a>"));
        _mockStore.ReadStoredVersion(VersionFilePath)
            .Returns(Result.Success<StoredVersionInfo?>(StoredInfo401));

        // Act
        Result<GameUpdateCheckSummary> result = await _checker.CheckForUpdateAsync(VersionFilePath);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ForumVersion.ShouldBe("40.2");
    }

    [Fact]
    public async Task CheckForUpdateAsync_ShouldParseCorrectly_WhenDottedVersion()
    {
        // Arrange
        _mockFetcher.FetchReleaseNotesPageAsync()
            .Returns(Result.Success(ForumPageWithVersion("40.2.1")));
        _mockStore.ReadStoredVersion(VersionFilePath)
            .Returns(Result.Success<StoredVersionInfo?>(StoredInfo401));

        // Act
        Result<GameUpdateCheckSummary> result = await _checker.CheckForUpdateAsync(VersionFilePath);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ForumVersion.ShouldBe("40.2.1");
    }

    // ───────────────────────────── Constructor ─────────────────────────────

    [Fact]
    public void Constructor_ShouldThrow_WhenNullFetcher()
    {
        // Act & Assert
        Action act = () => new GameUpdateChecker(null!, _mockStore, _mockLogger);
        act.ShouldThrow<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenNullStore()
    {
        // Act & Assert
        Action act = () => new GameUpdateChecker(_mockFetcher, null!, _mockLogger);
        act.ShouldThrow<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenNullLogger()
    {
        // Act & Assert
        Action act = () => new GameUpdateChecker(_mockFetcher, _mockStore, null!);
        act.ShouldThrow<ArgumentNullException>();
    }

    private static string ForumPageWithVersion(string version) =>
        $"""
        <html><body>
        <div class="structItem-title">
            <a href="/threads/update-{version}.12345/">Update {version} Release Notes</a>
        </div>
        </body></html>
        """;
}
