using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Features.UpdateCheck;
using LotroKoniecDev.Domain.Core.BuildingBlocks;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Primitives.Enums;

namespace LotroKoniecDev.Tests.Unit.Tests.Features;

public sealed class GameUpdateCheckerTests
{
    private const string VersionFilePath = @"C:\temp\version.txt";

    private readonly IForumPageFetcher _mockFetcher;
    private readonly IVersionFileStore _mockStore;
    private readonly GameUpdateChecker _checker;

    public GameUpdateCheckerTests()
    {
        _mockFetcher = Substitute.For<IForumPageFetcher>();
        _mockStore = Substitute.For<IVersionFileStore>();
        _checker = new GameUpdateChecker(_mockFetcher, _mockStore);
    }

    [Fact]
    public async Task CheckForUpdateAsync_NewVersionDetected_ShouldReturnUpdateDetected()
    {
        // Arrange
        _mockFetcher.FetchReleaseNotesPageAsync()
            .Returns(Result.Success(ForumPageWithVersion("40.2")));
        _mockStore.ReadLastKnownVersion(VersionFilePath)
            .Returns(Result.Success<string?>("40.1"));
        _mockStore.SaveVersion(VersionFilePath, "40.2")
            .Returns(Result.Success());

        // Act
        Result<GameUpdateCheckResult> result = await _checker.CheckForUpdateAsync(VersionFilePath);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.UpdateDetected.Should().BeTrue();
        result.Value.CurrentVersion.Should().Be("40.2");
        result.Value.PreviousVersion.Should().Be("40.1");

        _mockStore.Received(1).SaveVersion(VersionFilePath, "40.2");
    }

    [Fact]
    public async Task CheckForUpdateAsync_SameVersion_ShouldReturnNoUpdate()
    {
        // Arrange
        _mockFetcher.FetchReleaseNotesPageAsync()
            .Returns(Result.Success(ForumPageWithVersion("40.1")));
        _mockStore.ReadLastKnownVersion(VersionFilePath)
            .Returns(Result.Success<string?>("40.1"));

        // Act
        Result<GameUpdateCheckResult> result = await _checker.CheckForUpdateAsync(VersionFilePath);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.UpdateDetected.Should().BeFalse();
        result.Value.CurrentVersion.Should().Be("40.1");
        result.Value.PreviousVersion.Should().Be("40.1");
    }

    [Fact]
    public async Task CheckForUpdateAsync_SameVersion_ShouldNotSaveVersion()
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
    public async Task CheckForUpdateAsync_FirstRun_ShouldReturnUpdateDetected()
    {
        // Arrange
        _mockFetcher.FetchReleaseNotesPageAsync()
            .Returns(Result.Success(ForumPageWithVersion("40.1")));
        _mockStore.ReadLastKnownVersion(VersionFilePath)
            .Returns(Result.Success<string?>(null));
        _mockStore.SaveVersion(VersionFilePath, "40.1")
            .Returns(Result.Success());

        // Act
        Result<GameUpdateCheckResult> result = await _checker.CheckForUpdateAsync(VersionFilePath);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.UpdateDetected.Should().BeTrue();
        result.Value.CurrentVersion.Should().Be("40.1");
        result.Value.PreviousVersion.Should().BeNull();
    }

    [Fact]
    public async Task CheckForUpdateAsync_NetworkError_ShouldReturnFailure()
    {
        // Arrange
        Error networkError = new Error("GameUpdateCheck.NetworkError", "Connection refused", ErrorType.IoError);
        _mockFetcher.FetchReleaseNotesPageAsync()
            .Returns(Result.Failure<string>(networkError));

        // Act
        Result<GameUpdateCheckResult> result = await _checker.CheckForUpdateAsync(VersionFilePath);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("GameUpdateCheck.NetworkError");
    }

    [Fact]
    public async Task CheckForUpdateAsync_VersionNotFoundInPage_ShouldReturnFailure()
    {
        // Arrange
        _mockFetcher.FetchReleaseNotesPageAsync()
            .Returns(Result.Success("<html><body>No version here</body></html>"));
        _mockStore.ReadLastKnownVersion(Arg.Any<string>()).Returns(Result.Success<string?>("40.1"));

        // Act
        Result<GameUpdateCheckResult> result = await _checker.CheckForUpdateAsync(VersionFilePath);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("GameUpdateCheck.Failed");
    }

    [Fact]
    public async Task CheckForUpdateAsync_VersionFileReadError_ShouldReturnFailure()
    {
        // Arrange
        _mockFetcher.FetchReleaseNotesPageAsync()
            .Returns(Result.Success(ForumPageWithVersion("40.1")));

        Error readError = new Error("GameUpdateCheck.VersionFileError", "Access denied", ErrorType.IoError);
        _mockStore.ReadLastKnownVersion(VersionFilePath)
            .Returns(Result.Failure<string?>(readError));

        // Act
        Result<GameUpdateCheckResult> result = await _checker.CheckForUpdateAsync(VersionFilePath);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("GameUpdateCheck.VersionFileError");
    }

    [Fact]
    public async Task CheckForUpdateAsync_VersionFileSaveError_ShouldReturnFailure()
    {
        // Arrange
        _mockFetcher.FetchReleaseNotesPageAsync()
            .Returns(Result.Success(ForumPageWithVersion("40.2")));
        _mockStore.ReadLastKnownVersion(VersionFilePath)
            .Returns(Result.Success<string?>("40.1"));

        Error saveError = new Error("GameUpdateCheck.VersionFileError", "Disk full", ErrorType.IoError);
        _mockStore.SaveVersion(VersionFilePath, "40.2")
            .Returns(Result.Failure(saveError));

        // Act
        Result<GameUpdateCheckResult> result = await _checker.CheckForUpdateAsync(VersionFilePath);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("GameUpdateCheck.VersionFileError");
    }

    [Fact]
    public async Task CheckForUpdateAsync_NullPath_ShouldThrow()
    {
        // Act
        Func<Task> act = () => _checker.CheckForUpdateAsync(null!);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task CheckForUpdateAsync_EmptyPath_ShouldThrow()
    {
        // Act
        Func<Task> act = () => _checker.CheckForUpdateAsync("");

        // Assert
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task CheckForUpdateAsync_MultipleVersionsInHtml_ShouldPickFirst()
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
        _mockStore.SaveVersion(VersionFilePath, "40.2")
            .Returns(Result.Success());

        // Act
        Result<GameUpdateCheckResult> result = await _checker.CheckForUpdateAsync(VersionFilePath);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.CurrentVersion.Should().Be("40.2");
    }

    [Fact]
    public async Task CheckForUpdateAsync_CaseInsensitiveHtml_ShouldMatch()
    {
        // Arrange
        _mockFetcher.FetchReleaseNotesPageAsync()
            .Returns(Result.Success("<a>UPDATE 40.1 RELEASE NOTES</a>"));
        _mockStore.ReadLastKnownVersion(VersionFilePath)
            .Returns(Result.Success<string?>(null));
        _mockStore.SaveVersion(VersionFilePath, "40.1")
            .Returns(Result.Success());

        // Act
        Result<GameUpdateCheckResult> result = await _checker.CheckForUpdateAsync(VersionFilePath);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.CurrentVersion.Should().Be("40.1");
    }

    [Fact]
    public async Task CheckForUpdateAsync_DottedVersion_ShouldParseCorrectly()
    {
        // Arrange
        _mockFetcher.FetchReleaseNotesPageAsync()
            .Returns(Result.Success(ForumPageWithVersion("40.2.1")));
        _mockStore.ReadLastKnownVersion(VersionFilePath)
            .Returns(Result.Success<string?>(null));
        _mockStore.SaveVersion(VersionFilePath, "40.2.1")
            .Returns(Result.Success());

        // Act
        Result<GameUpdateCheckResult> result = await _checker.CheckForUpdateAsync(VersionFilePath);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.CurrentVersion.Should().Be("40.2.1");
    }

    [Fact]
    public void Constructor_NullFetcher_ShouldThrow()
    {
        // Act & Assert
        Action act = () => new GameUpdateChecker(null!, _mockStore);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullStore_ShouldThrow()
    {
        // Act & Assert
        Action act = () => new GameUpdateChecker(_mockFetcher, null!);
        act.Should().Throw<ArgumentNullException>();
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
