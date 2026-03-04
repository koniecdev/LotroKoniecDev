using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Features.UpdateChecking;
using LotroKoniecDev.Domain.Core.BuildingBlocks;
using LotroKoniecDev.Domain.Core.Errors;
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

    public GameUpdateCheckerTests()
    {
        _mockFetcher = Substitute.For<IForumPageFetcher>();
        _mockStore = Substitute.For<IGameVersionFileStore>();
        _mockLogger = Substitute.For<ILogger<GameUpdateChecker>>();
        _checker = new GameUpdateChecker(_mockFetcher, _mockStore, _mockLogger);
    }

    // ───────────────────────────── CheckForUpdateAsync — normal flow ─────────────────────────────

    [Fact]
    public async Task CheckForUpdateAsync_ShouldReturnUpdateDetected_WhenNewVersionDetected()
    {
        // Arrange
        _mockFetcher.FetchReleaseNotesPageAsync()
            .Returns(Result.Success(ForumPageWithVersion("40.2")));
        _mockStore.ReadLastKnownVersion(VersionFilePath)
            .Returns(Result.Success<string?>("40.1"));

        // Act
        Result<GameUpdateCheckSummary> result = await _checker.CheckForUpdateAsync(VersionFilePath);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.UpdateDetected.ShouldBeTrue();
        result.Value.ForumVersion.ShouldBe("40.2");
        result.Value.StoredVersion.ShouldBe("40.1");
    }

    [Fact]
    public async Task CheckForUpdateAsync_ShouldNotSaveVersion_WhenNewVersionDetected()
    {
        // Arrange
        _mockFetcher.FetchReleaseNotesPageAsync()
            .Returns(Result.Success(ForumPageWithVersion("40.2")));
        _mockStore.ReadLastKnownVersion(VersionFilePath)
            .Returns(Result.Success<string?>("40.1"));

        // Act
        await _checker.CheckForUpdateAsync(VersionFilePath);

        // Assert — CheckForUpdateAsync only reports, never saves
        _mockStore.DidNotReceive().SaveVersion(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task CheckForUpdateAsync_ShouldReturnNoUpdate_WhenSameVersion()
    {
        // Arrange
        _mockFetcher.FetchReleaseNotesPageAsync()
            .Returns(Result.Success(ForumPageWithVersion("40.1")));
        _mockStore.ReadLastKnownVersion(VersionFilePath)
            .Returns(Result.Success<string?>("40.1"));

        // Act
        Result<GameUpdateCheckSummary> result = await _checker.CheckForUpdateAsync(VersionFilePath);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.UpdateDetected.ShouldBeFalse();
        result.Value.ForumVersion.ShouldBe("40.1");
        result.Value.StoredVersion.ShouldBe("40.1");
    }

    [Fact]
    public async Task CheckForUpdateAsync_ShouldNotSaveVersion_WhenSameVersion()
    {
        // Arrange
        _mockFetcher.FetchReleaseNotesPageAsync()
            .Returns(Result.Success(ForumPageWithVersion("40.1")));
        _mockStore.ReadLastKnownVersion(VersionFilePath)
            .Returns(Result.Success<string?>("40.1"));

        // Act
        await _checker.CheckForUpdateAsync(VersionFilePath);

        // Assert
        _mockStore.DidNotReceive().SaveVersion(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task CheckForUpdateAsync_ShouldReturnFailure_WhenVersionFileReadError()
    {
        // Arrange
        Error readError = new("GameUpdateCheck.VersionFileError", "Access denied", ErrorType.IoError);
        _mockStore.ReadLastKnownVersion(VersionFilePath)
            .Returns(Result.Failure<string?>(readError));

        // Act
        Result<GameUpdateCheckSummary> result = await _checker.CheckForUpdateAsync(VersionFilePath);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("GameUpdateCheck.VersionFileError");
    }

    // ───────────────────────────── CheckForUpdateAsync — graceful degradation ─────────────────────────────

    [Fact]
    public async Task CheckForUpdateAsync_ShouldReturnNoUpdate_WhenForumFetchFails()
    {
        // Arrange
        _mockStore.ReadLastKnownVersion(VersionFilePath)
            .Returns(Result.Success<string?>("40.1"));
        Error networkError = new("GameUpdateCheck.NetworkError", "Connection refused", ErrorType.IoError);
        _mockFetcher.FetchReleaseNotesPageAsync()
            .Returns(Result.Failure<string>(networkError));

        // Act
        Result<GameUpdateCheckSummary> result = await _checker.CheckForUpdateAsync(VersionFilePath);

        // Assert — forum fail is graceful, never blocks gaming
        result.IsSuccess.ShouldBeTrue();
        result.Value.UpdateDetected.ShouldBeFalse();
        result.Value.ForumVersion.ShouldBeNull();
        result.Value.StoredVersion.ShouldBe("40.1");
        result.Value.ForumCheckSucceeded.ShouldBeFalse();
    }

    [Fact]
    public async Task CheckForUpdateAsync_ShouldReturnNoUpdate_WhenVersionNotFoundInPage()
    {
        // Arrange
        _mockFetcher.FetchReleaseNotesPageAsync()
            .Returns(Result.Success("<html><body>No version here</body></html>"));
        _mockStore.ReadLastKnownVersion(VersionFilePath)
            .Returns(Result.Success<string?>("40.1"));

        // Act
        Result<GameUpdateCheckSummary> result = await _checker.CheckForUpdateAsync(VersionFilePath);

        // Assert — page format change is graceful, never blocks gaming
        result.IsSuccess.ShouldBeTrue();
        result.Value.UpdateDetected.ShouldBeFalse();
        result.Value.ForumVersion.ShouldBeNull();
        result.Value.StoredVersion.ShouldBe("40.1");
    }

    // ───────────────────────────── CheckForUpdateAsync — first run ─────────────────────────────

    [Fact]
    public async Task CheckForUpdateAsync_ShouldReturnUpdateDetected_WhenFirstRunAndForumSucceeds()
    {
        // Arrange — no version.txt yet, forum works
        _mockStore.ReadLastKnownVersion(VersionFilePath)
            .Returns(Result.Success<string?>(null));
        _mockFetcher.FetchReleaseNotesPageAsync()
            .Returns(Result.Success(ForumPageWithVersion("40.2")));

        // Act
        Result<GameUpdateCheckSummary> result = await _checker.CheckForUpdateAsync(VersionFilePath);

        // Assert — first run with forum OK triggers update flow to establish baseline
        result.IsSuccess.ShouldBeTrue();
        result.Value.UpdateDetected.ShouldBeTrue();
        result.Value.ForumVersion.ShouldBe("40.2");
        result.Value.StoredVersion.ShouldBeNull();
        result.Value.IsFirstLaunch.ShouldBeTrue();
    }

    [Fact]
    public async Task CheckForUpdateAsync_ShouldReturnNoUpdate_WhenFirstRunAndForumFails()
    {
        // Arrange — no version.txt, AND forum unreachable
        _mockStore.ReadLastKnownVersion(VersionFilePath)
            .Returns(Result.Success<string?>(null));
        Error networkError = new("GameUpdateCheck.NetworkError", "Connection refused", ErrorType.IoError);
        _mockFetcher.FetchReleaseNotesPageAsync()
            .Returns(Result.Failure<string>(networkError));

        // Act
        Result<GameUpdateCheckSummary> result = await _checker.CheckForUpdateAsync(VersionFilePath);

        // Assert — zero info, graceful: just let the user play
        result.IsSuccess.ShouldBeTrue();
        result.Value.UpdateDetected.ShouldBeFalse();
        result.Value.ForumVersion.ShouldBeNull();
        result.Value.StoredVersion.ShouldBeNull();
        result.Value.IsFirstLaunch.ShouldBeTrue();
        result.Value.ForumCheckSucceeded.ShouldBeFalse();
    }

    [Fact]
    public async Task CheckForUpdateAsync_ShouldReturnNoUpdate_WhenFirstRunAndVersionNotFoundInPage()
    {
        // Arrange — no version.txt, forum reachable but page format changed
        _mockStore.ReadLastKnownVersion(VersionFilePath)
            .Returns(Result.Success<string?>(null));
        _mockFetcher.FetchReleaseNotesPageAsync()
            .Returns(Result.Success("<html><body>No version here</body></html>"));

        // Act
        Result<GameUpdateCheckSummary> result = await _checker.CheckForUpdateAsync(VersionFilePath);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.UpdateDetected.ShouldBeFalse();
        result.Value.ForumVersion.ShouldBeNull();
        result.Value.StoredVersion.ShouldBeNull();
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
        _mockStore.ReadLastKnownVersion(VersionFilePath)
            .Returns(Result.Success<string?>("40.1"));

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
        _mockStore.ReadLastKnownVersion(VersionFilePath)
            .Returns(Result.Success<string?>("40.1"));

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
        _mockStore.ReadLastKnownVersion(VersionFilePath)
            .Returns(Result.Success<string?>("40.1"));

        // Act
        Result<GameUpdateCheckSummary> result = await _checker.CheckForUpdateAsync(VersionFilePath);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ForumVersion.ShouldBe("40.2.1");
    }

    // ───────────────────────────── ConfirmUpdateInstalled ─────────────────────────────

    [Fact]
    public void ConfirmUpdateInstalled_ShouldSaveAndSucceed_WhenFirstRun()
    {
        // Arrange
        _mockStore.SaveVersion(VersionFilePath, "40.2")
            .Returns(Result.Success());
        DatVersionInfo previousVersion = new(100, 200);
        DatVersionInfo currentVersion = new(100, 201);

        // Act
        Result result = _checker.ConfirmUpdateInstalled(VersionFilePath, "40.2", isFirstRun: true, previousVersion, currentVersion);

        // Assert — first run trusts forum version, saves immediately, no vnum check
        result.IsSuccess.ShouldBeTrue();
        _mockStore.Received(1).SaveVersion(VersionFilePath, "40.2");
    }

    [Fact]
    public void ConfirmUpdateInstalled_ShouldSaveForumVersion_WhenDatVersionChanged()
    {
        // Arrange
        DatVersionInfo previousVersion = new(100, 200);
        DatVersionInfo currentVersion = new(100, 201);
        _mockStore.SaveVersion(VersionFilePath, "40.2")
            .Returns(Result.Success());

        // Act
        Result result = _checker.ConfirmUpdateInstalled(VersionFilePath, "40.2", isFirstRun: false, previousVersion, currentVersion);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        _mockStore.Received(1).SaveVersion(VersionFilePath, "40.2");
    }

    [Fact]
    public void ConfirmUpdateInstalled_ShouldSaveAndSucceed_WhenDatVersionUnchanged()
    {
        // Arrange — vnum unchanged (game may already be updated, or user skipped update)
        DatVersionInfo sameVersion = new(100, 200);
        _mockStore.SaveVersion(VersionFilePath, "40.2")
            .Returns(Result.Success());

        // Act
        Result result = _checker.ConfirmUpdateInstalled(VersionFilePath, "40.2", isFirstRun: false, sameVersion, sameVersion);

        // Assert — saves version and continues (graceful: re-patch will fix translations)
        result.IsSuccess.ShouldBeTrue();
        _mockStore.Received(1).SaveVersion(VersionFilePath, "40.2");
    }

    [Fact]
    public void ConfirmUpdateInstalled_ShouldReturnFailure_WhenSaveVersionFails()
    {
        // Arrange
        DatVersionInfo previousVersion = new(100, 200);
        DatVersionInfo currentVersion = new(100, 201);
        Error saveError = new("GameUpdateCheck.VersionFileError", "Disk full", ErrorType.IoError);
        _mockStore.SaveVersion(VersionFilePath, "40.2")
            .Returns(Result.Failure(saveError));

        // Act
        Result result = _checker.ConfirmUpdateInstalled(VersionFilePath, "40.2", isFirstRun: false, previousVersion, currentVersion);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("GameUpdateCheck.VersionFileError");
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
