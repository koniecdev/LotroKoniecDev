using LotroKoniecDev.Application;
using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Features.AppUpdating;
using LotroKoniecDev.Domain.Core.BuildingBlocks;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Primitives.Enums;
using Microsoft.Extensions.Logging.Abstractions;

namespace LotroKoniecDev.Tests.Unit.Tests.Features;

public sealed class InstallAppUpdateCommandHandlerTests
{
    private const string InstallerName = "LotroPoPolsku-Setup-0.3.0.exe";

    private const string Hash = "aa0c3c6a3b0e6f3e9c1d2b7a8e5f4c3b2a1908f7e6d5c4b3a29180f7e6d5c4b3";

    private static readonly DesktopVersion Version = new(0, 3, 0);

    private static readonly Uri InstallerUri =
        new("https://github.com/koniecdev/LotroKoniecDev/releases/download/desktop-v0.3.0/LotroPoPolsku-Setup-0.3.0.exe");

    private static readonly Uri ChecksumUri =
        new("https://github.com/koniecdev/LotroKoniecDev/releases/download/desktop-v0.3.0/LotroPoPolsku-Setup-0.3.0.exe.sha256");

    private static readonly string DownloadFolder = Path.Combine("updates", "abc");

    private static readonly string LogPath = Path.Combine("LotroPoPolsku", "update-install.log");

    private readonly IGitHubReleaseClient _releaseClient = Substitute.For<IGitHubReleaseClient>();
    private readonly IAppUpdateWorkspace _workspace = Substitute.For<IAppUpdateWorkspace>();
    private readonly IInstallerFileOpener _opener = Substitute.For<IInstallerFileOpener>();
    private readonly IOpenedInstaller _installer = Substitute.For<IOpenedInstaller>();
    private readonly IProgress<OperationProgress> _progress = Substitute.For<IProgress<OperationProgress>>();
    private readonly InstallAppUpdateCommandHandler _sut;

    [Fact]
    public async Task Handle_NullCommand_Throws()
    {
        await Should.ThrowAsync<ArgumentNullException>(() => _sut.Handle(null!, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Handle_AMatchingInstaller_StartsItWithThePinnedArgumentsAndTheLogPath()
    {
        // Act
        Result result = await Install(Command());

        // Assert: the start is the whole point and returns nothing else to look at.
        result.IsSuccess.ShouldBeTrue();
        _installer.Received(1).Start(
            $"/SILENT /NOCANCEL /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /FORCECLOSEAPPLICATIONS \"/LOG={LogPath}\"");
    }

    [Fact]
    public async Task Handle_AMatchingInstaller_DownloadsItIntoTheFreshFolderUnderItsReleaseName()
    {
        // Act
        await Install(Command());

        // Assert
        _opener.Received(1).Open(Path.Combine(DownloadFolder, InstallerName));
    }

    [Fact]
    public async Task Handle_TheHashInUpperCase_StillMatches()
    {
        // Arrange
        _releaseClient.DownloadTextAsync(ChecksumUri, Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success($"{Hash.ToUpperInvariant()}  {InstallerName}\n"));

        // Act
        Result result = await Install(Command());

        // Assert
        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Handle_AHashMismatch_RefusesAndNeverStartsTheInstaller()
    {
        // Arrange
        _installer.Sha256Hex.Returns("bb" + Hash[2..]);

        // Act
        Result result = await Install(Command());

        // Assert
        result.Error.Code.ShouldBe(DomainErrors.AppUpdate.HashMismatchCode);
        _installer.DidNotReceive().Start(Arg.Any<string>());
    }

    [Fact]
    public async Task Handle_AnyEnding_ReleasesTheHeldInstaller()
    {
        // Arrange
        _installer.Sha256Hex.Returns("bb" + Hash[2..]);

        // Act
        await Install(Command());

        // Assert: the held handle has no other trace once the handler returns.
        _installer.Received(1).Dispose();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a checksum")]
    [InlineData(Hash + "  LotroPoPolsku-Setup-0.2.0.exe\n")]
    public async Task Handle_ABrokenChecksumFile_RefusesBeforeTheInstallerIsDownloaded(string content)
    {
        // Arrange
        _releaseClient.DownloadTextAsync(ChecksumUri, Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(content));

        // Act
        Result result = await Install(Command());

        // Assert
        result.Error.Code.ShouldBe(DomainErrors.AppUpdate.ChecksumFileInvalidCode);
        await _releaseClient.DidNotReceive().DownloadFileAsync(
            Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<IProgress<OperationProgress>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_TheChecksumFile_IsReadWithItsOneKibLimit()
    {
        // Act
        await Install(Command());

        // Assert
        await _releaseClient.Received(1).DownloadTextAsync(ChecksumUri, 1024, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_TheInstaller_IsDownloadedWithIts256MibLimitAndTheSharedProgress()
    {
        // Act
        await Install(Command());

        // Assert
        await _releaseClient.Received(1).DownloadFileAsync(
            InstallerUri, Path.Combine(DownloadFolder, InstallerName), 256L * 1024 * 1024, _progress, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("folder")]
    [InlineData("checksum")]
    [InlineData("installer")]
    [InlineData("log")]
    [InlineData("open")]
    public async Task Handle_AStepBeforeTheStartFails_ReturnsItsErrorAndStartsNothing(string step)
    {
        // Arrange
        Error error = new($"AppUpdate.Test.{step}", "failed");
        switch (step)
        {
            case "folder":
                _workspace.CreateDownloadFolder().Returns(Result.Failure<string>(error));
                break;
            case "checksum":
                _releaseClient.DownloadTextAsync(Arg.Any<Uri>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
                    .Returns(Result.Failure<string>(error));
                break;
            case "installer":
                _releaseClient.DownloadFileAsync(
                        Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<IProgress<OperationProgress>>(), Arg.Any<CancellationToken>())
                    .Returns(Result.Failure(error));
                break;
            case "log":
                _workspace.PrepareInstallerLogPath().Returns(Result.Failure<string>(error));
                break;
            default:
                _opener.Open(Arg.Any<string>()).Returns(Result.Failure<IOpenedInstaller>(error));
                break;
        }

        // Act
        Result result = await Install(Command());

        // Assert
        result.Error.ShouldBe(error);
        _installer.DidNotReceive().Start(Arg.Any<string>());
    }

    [Fact]
    public async Task Handle_AHashMismatch_DeletesTheRefusedFileAfterReleasingIt()
    {
        // Arrange
        _installer.Sha256Hex.Returns("bb" + Hash[2..]);

        // Act
        await Install(Command());

        // Assert: Windows cannot delete a file that is still held, so the order matters.
        Received.InOrder(() =>
        {
            _installer.Dispose();
            _workspace.DeleteDownloadFolder(DownloadFolder);
        });
    }

    [Theory]
    [InlineData("checksum")]
    [InlineData("installer")]
    [InlineData("start")]
    public async Task Handle_AFailureAfterTheFolderWasMade_DeletesTheFolder(string step)
    {
        // Arrange
        Error error = new($"AppUpdate.Test.{step}", "failed");
        switch (step)
        {
            case "checksum":
                _releaseClient.DownloadTextAsync(Arg.Any<Uri>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
                    .Returns(Result.Failure<string>(error));
                break;
            case "installer":
                _releaseClient.DownloadFileAsync(
                        Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<IProgress<OperationProgress>>(), Arg.Any<CancellationToken>())
                    .Returns(Result.Failure(error));
                break;
            default:
                _installer.Start(Arg.Any<string>()).Returns(Result.Failure(error));
                break;
        }

        // Act
        await Install(Command());

        // Assert: the folder is a side effect the result does not show.
        _workspace.Received(1).DeleteDownloadFolder(DownloadFolder);
    }

    [Fact]
    public async Task Handle_AStartedInstaller_KeepsItsFolder()
    {
        // Act
        await Install(Command());

        // Assert: the installer runs from that folder.
        _workspace.DidNotReceive().DeleteDownloadFolder(Arg.Any<string>());
    }

    [Fact]
    public async Task Handle_NoFolderCouldBeMade_DeletesNothing()
    {
        // Arrange
        _workspace.CreateDownloadFolder()
            .Returns(Result.Failure<string>(DomainErrors.AppUpdate.FileSystemError("updates", "denied")));

        // Act
        await Install(Command());

        // Assert
        _workspace.DidNotReceive().DeleteDownloadFolder(Arg.Any<string>());
    }

    [Fact]
    public async Task Handle_TheAdminPromptWasCancelled_ReturnsThatReason()
    {
        // Arrange
        _installer.Start(Arg.Any<string>()).Returns(Result.Failure(DomainErrors.AppUpdate.InstallerStartCancelled));

        // Act
        Result result = await Install(Command());

        // Assert
        result.Error.Code.ShouldBe(DomainErrors.AppUpdate.InstallerStartCancelledCode);
    }

    public static TheoryData<InstallAppUpdateCommand> CommandsThatAreRefused() =>
    [
        new(DesktopVersion.DevelopmentBuild, InstallerUri, ChecksumUri),
        new(null!, InstallerUri, ChecksumUri),
        new(Version, null!, ChecksumUri),
        new(Version, InstallerUri, null!),
        new(Version, ChecksumUri, InstallerUri),
        new(new DesktopVersion(0, 3, 1), InstallerUri, ChecksumUri),
        new(Version, new Uri("http://github.com/koniecdev/LotroKoniecDev/releases/download/desktop-v0.3.0/LotroPoPolsku-Setup-0.3.0.exe"), ChecksumUri),
        new(Version, new Uri("https://evil.example.com/koniecdev/LotroKoniecDev/releases/download/desktop-v0.3.0/LotroPoPolsku-Setup-0.3.0.exe"), ChecksumUri),
        new(Version, new Uri("https://user@github.com/koniecdev/LotroKoniecDev/releases/download/desktop-v0.3.0/LotroPoPolsku-Setup-0.3.0.exe"), ChecksumUri),
        new(Version, new Uri("https://github.com/someone/else/releases/download/desktop-v0.3.0/LotroPoPolsku-Setup-0.3.0.exe"), ChecksumUri),
        new(Version, InstallerUri, new Uri("https://github.com/koniecdev/LotroKoniecDev/releases/download/desktop-v0.3.0/LotroPoPolsku-Setup-0.3.0.exe.sha256?x=1"))
    ];

    [Theory]
    [MemberData(nameof(CommandsThatAreRefused))]
    public async Task Handle_AnAddressOrVersionTheCheckWouldNeverOffer_IsAValidationErrorAndDownloadsNothing(
        InstallAppUpdateCommand command)
    {
        // Act
        Result result = await Install(command);

        // Assert
        result.Error.Type.ShouldBe(ErrorType.Validation);
        await _releaseClient.DidNotReceive().DownloadTextAsync(Arg.Any<Uri>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    public InstallAppUpdateCommandHandlerTests()
    {
        _workspace.CreateDownloadFolder().Returns(Result.Success(DownloadFolder));
        _workspace.PrepareInstallerLogPath().Returns(Result.Success(LogPath));
        _releaseClient.DownloadTextAsync(ChecksumUri, Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success($"{Hash}  {InstallerName}\n"));
        _releaseClient.DownloadFileAsync(
                Arg.Any<Uri>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<IProgress<OperationProgress>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        _installer.Sha256Hex.Returns(Hash);
        _installer.Start(Arg.Any<string>()).Returns(Result.Success());
        _opener.Open(Arg.Any<string>()).Returns(Result.Success(_installer));

        _sut = new InstallAppUpdateCommandHandler(
            _releaseClient,
            _workspace,
            _opener,
            _progress,
            new InstallAppUpdateCommandValidator(),
            NullLogger<InstallAppUpdateCommandHandler>.Instance);
    }

    private static InstallAppUpdateCommand Command() => new(Version, InstallerUri, ChecksumUri);

    private Task<Result> Install(InstallAppUpdateCommand command) => _sut.Handle(command, CancellationToken.None).AsTask();
}
