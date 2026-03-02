using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Abstractions.DatFilesServices;
using LotroKoniecDev.Application.Features.UpdateChecking;
using LotroKoniecDev.Domain.Core.BuildingBlocks;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Domain.Models;
using LotroKoniecDev.Primitives.Enums;

namespace LotroKoniecDev.Tests.Unit.Tests.Features;

public sealed class GameUpdateCheckerTests
{
    private const string VersionFilePath = @"C:\temp\version.txt";
    private const string DatFilePath = @"C:\LOTRO\client_local_English.dat";

    private readonly IForumPageFetcher _mockFetcher;
    private readonly IDatVersionReader _mockDatVersionReader;
    private readonly IGameVersionFileStore _mockStore;
    private readonly GameUpdateChecker _checker;

    public GameUpdateCheckerTests()
    {
        _mockFetcher = Substitute.For<IForumPageFetcher>();
        _mockDatVersionReader = Substitute.For<IDatVersionReader>();
        _mockStore = Substitute.For<IGameVersionFileStore>();
        _checker = new GameUpdateChecker(_mockFetcher, _mockDatVersionReader, _mockStore);
    }

    [Fact]
    public async Task CheckForUpdateAsync_ShouldReturnUpdateDetected_WhenNewVersionDetected()
    {
        // Arrange
        _mockFetcher.FetchReleaseNotesPageAsync()
            .Returns(Result.Success(ForumPageWithVersion("40.2")));
        _mockStore.ReadLastKnownVersion(VersionFilePath)
            .Returns(Result.Success("40.1"));

        // Act
        Result<GameUpdateCheckSummary> result = await _checker.CheckForUpdateAsync(VersionFilePath);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.UpdateDetected.ShouldBeTrue();
        result.Value.CurrentVersion.ShouldBe("40.2");
        result.Value.PreviousVersion.ShouldBe("40.1");
    }

    [Fact]
    public async Task CheckForUpdateAsync_ShouldNotSaveVersion_WhenNewVersionDetected()
    {
        // Arrange
        _mockFetcher.FetchReleaseNotesPageAsync()
            .Returns(Result.Success(ForumPageWithVersion("40.2")));
        _mockStore.ReadLastKnownVersion(VersionFilePath)
            .Returns(Result.Success("40.1"));

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
            .Returns(Result.Success("40.1"));

        // Act
        Result<GameUpdateCheckSummary> result = await _checker.CheckForUpdateAsync(VersionFilePath);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.UpdateDetected.ShouldBeFalse();
        result.Value.CurrentVersion.ShouldBe("40.1");
        result.Value.PreviousVersion.ShouldBe("40.1");
    }

    [Fact]
    public async Task CheckForUpdateAsync_ShouldNotSaveVersion_WhenSameVersion()
    {
        // Arrange
        _mockFetcher.FetchReleaseNotesPageAsync()
            .Returns(Result.Success(ForumPageWithVersion("40.1")));
        _mockStore.ReadLastKnownVersion(VersionFilePath)
            .Returns(Result.Success("40.1"));

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
            .Returns(Result.Failure<string>(readError));

        // Act
        Result<GameUpdateCheckSummary> result = await _checker.CheckForUpdateAsync(VersionFilePath);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("GameUpdateCheck.VersionFileError");
    }

    [Fact]
    public async Task CheckForUpdateAsync_ShouldReturnFailure_WhenNetworkError()
    {
        // Arrange
        _mockStore.ReadLastKnownVersion(VersionFilePath)
            .Returns(Result.Success("40.1"));
        Error networkError = new("GameUpdateCheck.NetworkError", "Connection refused", ErrorType.IoError);
        _mockFetcher.FetchReleaseNotesPageAsync()
            .Returns(Result.Failure<string>(networkError));

        // Act
        Result<GameUpdateCheckSummary> result = await _checker.CheckForUpdateAsync(VersionFilePath);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("GameUpdateCheck.NetworkError");
    }

    [Fact]
    public async Task CheckForUpdateAsync_ShouldReturnFailure_WhenVersionNotFoundInPage()
    {
        // Arrange
        _mockFetcher.FetchReleaseNotesPageAsync()
            .Returns(Result.Success("<html><body>No version here</body></html>"));
        _mockStore.ReadLastKnownVersion(VersionFilePath)
            .Returns(Result.Success("40.1"));

        // Act
        Result<GameUpdateCheckSummary> result = await _checker.CheckForUpdateAsync(VersionFilePath);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("GameUpdateCheck.Failed");
    }

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
            .Returns(Result.Success("40.1"));

        // Act
        Result<GameUpdateCheckSummary> result = await _checker.CheckForUpdateAsync(VersionFilePath);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.CurrentVersion.ShouldBe("40.2");
    }

    [Fact]
    public async Task CheckForUpdateAsync_ShouldMatch_WhenCaseInsensitiveHtml()
    {
        // Arrange
        _mockFetcher.FetchReleaseNotesPageAsync()
            .Returns(Result.Success("<a>UPDATE 40.2 RELEASE NOTES</a>"));
        _mockStore.ReadLastKnownVersion(VersionFilePath)
            .Returns(Result.Success("40.1"));

        // Act
        Result<GameUpdateCheckSummary> result = await _checker.CheckForUpdateAsync(VersionFilePath);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.CurrentVersion.ShouldBe("40.2");
    }

    [Fact]
    public async Task CheckForUpdateAsync_ShouldParseCorrectly_WhenDottedVersion()
    {
        // Arrange
        _mockFetcher.FetchReleaseNotesPageAsync()
            .Returns(Result.Success(ForumPageWithVersion("40.2.1")));
        _mockStore.ReadLastKnownVersion(VersionFilePath)
            .Returns(Result.Success("40.1"));

        // Act
        Result<GameUpdateCheckSummary> result = await _checker.CheckForUpdateAsync(VersionFilePath);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.CurrentVersion.ShouldBe("40.2.1");
    }
    
    [Fact]
    public void ConfirmUpdateInstalled_ShouldSaveForumVersion_WhenDatVersionChanged()
    {
        // Arrange
        DatVersionInfo previousVersion = new(100, 200);
        DatVersionInfo currentVersion = new(100, 201);
        _mockDatVersionReader.ReadVersion(DatFilePath)
            .Returns(Result.Success(currentVersion));
        _mockStore.SaveVersion(VersionFilePath, "40.2")
            .Returns(Result.Success());

        // Act
        Result result = _checker.ConfirmUpdateInstalled(DatFilePath, VersionFilePath, "40.2", previousVersion);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        _mockStore.Received(1).SaveVersion(VersionFilePath, "40.2");
    }

    [Fact]
    public void ConfirmUpdateInstalled_ShouldReturnFailure_WhenDatVersionUnchanged()
    {
        // Arrange
        DatVersionInfo sameVersion = new(100, 200);
        _mockDatVersionReader.ReadVersion(DatFilePath)
            .Returns(Result.Success(sameVersion));

        // Act
        Result result = _checker.ConfirmUpdateInstalled(DatFilePath, VersionFilePath, "40.2", sameVersion);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(DomainErrors.GameUpdateCheck.GameUpdateRequired);
        _mockStore.DidNotReceive().SaveVersion(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public void ConfirmUpdateInstalled_ShouldReturnFailure_WhenReadVersionFails()
    {
        // Arrange
        DatVersionInfo previousVersion = new(100, 200);
        Error readError = new("DatFile.ReadFailed", "Cannot open DAT", ErrorType.IoError);
        _mockDatVersionReader.ReadVersion(DatFilePath)
            .Returns(Result.Failure<DatVersionInfo>(readError));

        // Act
        Result result = _checker.ConfirmUpdateInstalled(DatFilePath, VersionFilePath, "40.2", previousVersion);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("DatFile.ReadFailed");
    }

    [Fact]
    public void ConfirmUpdateInstalled_ShouldReturnFailure_WhenSaveVersionFails()
    {
        // Arrange
        DatVersionInfo previousVersion = new(100, 200);
        DatVersionInfo currentVersion = new(100, 201);
        _mockDatVersionReader.ReadVersion(DatFilePath)
            .Returns(Result.Success(currentVersion));
        Error saveError = new("GameUpdateCheck.VersionFileError", "Disk full", ErrorType.IoError);
        _mockStore.SaveVersion(VersionFilePath, "40.2")
            .Returns(Result.Failure(saveError));

        // Act
        Result result = _checker.ConfirmUpdateInstalled(DatFilePath, VersionFilePath, "40.2", previousVersion);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("GameUpdateCheck.VersionFileError");
    }

    // ───────────────────────────── Constructor ─────────────────────────────

    [Fact]
    public void Constructor_ShouldThrow_WhenNullFetcher()
    {
        // Act & Assert
        Action act = () => new GameUpdateChecker(null!, _mockDatVersionReader, _mockStore);
        act.ShouldThrow<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenNullDatVersionReader()
    {
        // Act & Assert
        Action act = () => new GameUpdateChecker(_mockFetcher, null!, _mockStore);
        act.ShouldThrow<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_ShouldThrow_WhenNullStore()
    {
        // Act & Assert
        Action act = () => new GameUpdateChecker(_mockFetcher, _mockDatVersionReader, null!);
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
