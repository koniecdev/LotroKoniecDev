using Avalonia.Threading;
using LotroKoniecDev.Application;
using LotroKoniecDev.Application.Features.GameLaunching;
using LotroKoniecDev.Application.Features.Patching;
using LotroKoniecDev.Application.Features.TranslationFileSyncing;
using LotroKoniecDev.DesktopApp.Presentation.ViewModels;
using LotroKoniecDev.DesktopApp.Tests.Unit.Shared;
using LotroKoniecDev.Domain.Core.BuildingBlocks;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;

namespace LotroKoniecDev.DesktopApp.Tests.Unit.Tests;

/// <summary>
/// Patch and Play over the closed handlers and ports, stubbed (#42). The progress bar posts to the UI
/// thread, so every test runs on the headless session's thread and runs its jobs before it reads the bar.
/// </summary>
[Collection(HeadlessSessionCollection.Name)]
public sealed class PatchAndPlayViewModelTests
{
    private const string Server = "https://tms.lotro-translator.pl/";

    private static readonly string OverrideFile = ViewModelBuilder.Rooted("Pendrive", "polish.txt");

    private readonly HeadlessSessionFixture _fixture;

    public static TheoryData<PatchIfNeededResponse, string> PatchOutcomes() => new()
    {
        { new PatchIfNeededResponse(true, true, 900, 100, 0, null), "Spolszczenie nałożone." },
        { new PatchIfNeededResponse(false, false, 0, 0, 0, null), "Plik spolszczenia się nie zmienił, więc pliki gry zostały bez zmian." },
        { new PatchIfNeededResponse(false, true, 95, 5, 2, null), "Gra przywróciła część angielskich tekstów. Spolszczenie nałożono na nie ponownie." }
    };

    public static TheoryData<PatchIfNeededResponse, string> PatchOutcomesThatWroteNothing() => new()
    {
        {
            new PatchIfNeededResponse(false, false, 0, 0, 2, "CannotOpen failed"),
            "Gra przywróciła część angielskich tekstów, ale nie udało się ponownie nałożyć na nie spolszczenia. Szczegóły są w pliku data\\LotroPoPolsku.log w folderze programu."
        },
        { new PatchIfNeededResponse(true, true, 0, 1200, 0, null), "Wczytano plik spolszczenia, ale nie nałożono z niego żadnego tekstu. Szczegóły są w pliku data\\LotroPoPolsku.log w folderze programu." },
        { new PatchIfNeededResponse(false, true, 0, 40, 2, null), "Wczytano plik spolszczenia, ale nie nałożono z niego żadnego tekstu. Szczegóły są w pliku data\\LotroPoPolsku.log w folderze programu." }
    };

    [Theory]
    [MemberData(nameof(PatchOutcomes))]
    public async Task PatchAsync_EachOutcome_ShowsItsPolishTextAndEndsTheBarAtHundred(PatchIfNeededResponse response, string expectedStatus)
    {
        // Arrange
        ViewModelBuilder builder = SavedGameUsingTheDownload();
        builder.PatchHandler.Handle(
                new PatchIfNeededCommand(
                    ViewModelBuilder.SavedDatFilePath, ViewModelBuilder.GameVersionFilePath, ViewModelBuilder.WorkingTranslationFilePath),
                Arg.Any<CancellationToken>())
            .Returns(Result.Success(response));

        // Act
        RunResult run = await PatchAsync(builder);

        // Assert: the stub answers only for the working copy under data, so the text proves the paths.
        run.Status.ShouldBe(expectedStatus);
        run.Percent.ShouldBe(100);
        run.IsIndeterminate.ShouldBeFalse();
        run.GateBusy.ShouldBeFalse();
    }

    [Theory]
    [MemberData(nameof(PatchOutcomesThatWroteNothing))]
    public async Task PatchAsync_AnOutcomeThatWroteNothing_ShowsItsPolishTextAndEndsTheBarAtZero(PatchIfNeededResponse response, string expectedStatus)
    {
        // Arrange: every row skipped (not approved, no source digest, English moved) or a failed repair,
        // after the patcher had already moved the bar.
        ViewModelBuilder builder = SavedGameUsingTheDownload();
        builder.PatchHandler.Handle(Arg.Any<PatchIfNeededCommand>(), Arg.Any<CancellationToken>())
            .Returns(_ => ReportThenAnswer(builder, Result.Success(response)));

        // Act
        RunResult run = await PatchAsync(builder);

        // Assert
        run.Status.ShouldBe(expectedStatus);
        run.Percent.ShouldBe(0);
        run.IsIndeterminate.ShouldBeFalse();
    }

    [Fact]
    public async Task PatchAsync_ASecondRun_StartsTheBarAtZeroAgain()
    {
        // Arrange: the first run leaves the bar at 100.
        ViewModelBuilder builder = SavedGameUsingTheDownload();
        TaskCompletionSource<Result<PatchIfNeededResponse>> secondPatch = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        builder.PatchHandler.Handle(Arg.Any<PatchIfNeededCommand>(), Arg.Any<CancellationToken>())
            .Returns(_ => ++calls == 1
                ? new ValueTask<Result<PatchIfNeededResponse>>(Result.Success(new PatchIfNeededResponse(true, true, 10, 0, 0, null)))
                : new ValueTask<Result<PatchIfNeededResponse>>(secondPatch.Task));

        (int AfterFirst, int DuringSecond, bool PercentShownDuringSecond) bar = await _fixture.Session.Dispatch(
            async () =>
            {
                PatchAndPlayViewModel viewModel = Create(builder);
                await viewModel.PatchAsync();
                Dispatcher.UIThread.RunJobs();
                int afterFirst = builder.Progress.Percent;

                // Act
                Task second = viewModel.PatchAsync();
                await WaitUntil(() => viewModel.StatusText == "Nakładam spolszczenie…");
                Dispatcher.UIThread.RunJobs();
                (int Percent, bool PercentShown) during = (builder.Progress.Percent, !builder.Progress.IsIndeterminate);

                secondPatch.SetResult(Result.Success(new PatchIfNeededResponse(false, false, 0, 0, 0, null)));
                await second;
                return (afterFirst, during.Percent, during.PercentShown);
            },
            CancellationToken.None);

        // Assert: during the patch step the bar shows the patcher's percent, starting from 0.
        bar.AfterFirst.ShouldBe(100);
        bar.DuringSecond.ShouldBe(0);
        bar.PercentShownDuringSecond.ShouldBeTrue();
    }

    [Fact]
    public async Task PatchAsync_WithNoOverride_DownloadsIntoTheDataFolderFromTheSavedServer()
    {
        // Arrange
        ViewModelBuilder builder = SavedGameUsingTheDownload();
        builder.SyncHandler.Handle(Arg.Any<SyncTranslationFileCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<TranslationFileSyncResponse>(Error.Failure("Test.WrongCommand", "not the expected command")));
        builder.SyncHandler.Handle(
                new SyncTranslationFileCommand(Server, ViewModelBuilder.DownloadedTranslationFilePath), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new TranslationFileSyncResponse(TranslationFileSyncOutcome.Updated, null)));
        builder.Workspace.UseTranslationFile(Arg.Any<string>())
            .Returns(Result.Failure(DomainErrors.DesktopPatchWorkspace.FileSystemError("wrong source", "not the download")));
        builder.Workspace.UseTranslationFile(ViewModelBuilder.DownloadedTranslationFilePath).Returns(Result.Success());

        // Act
        RunResult run = await PatchAsync(builder);

        // Assert
        run.Status.ShouldBe("Plik spolszczenia się nie zmienił, więc pliki gry zostały bez zmian.");
    }

    [Fact]
    public async Task PatchAsync_WithAnOverride_UsesThePlayersFileAndNeverDownloads()
    {
        // Arrange
        ViewModelBuilder builder = SavedGameWithTheOverride();
        builder.Workspace.UseTranslationFile(Arg.Any<string>())
            .Returns(Result.Failure(DomainErrors.DesktopPatchWorkspace.FileSystemError("wrong source", "not the override")));
        builder.Workspace.UseTranslationFile(OverrideFile).Returns(Result.Success());

        // Act
        RunResult run = await PatchAsync(builder);

        // Assert
        run.Status.ShouldBe("Plik spolszczenia się nie zmienił, więc pliki gry zostały bez zmian.");
        await builder.SyncHandler.DidNotReceive().Handle(Arg.Any<SyncTranslationFileCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PatchAsync_TheOverrideIsGone_NamesTheFileSaysHowToClearItAndNeverFallsBackToTheDownload()
    {
        // Arrange
        ViewModelBuilder builder = SavedGameWithTheOverride();
        builder.Workspace.UseTranslationFile(OverrideFile)
            .Returns(Result.Failure(DomainErrors.DesktopPatchWorkspace.SourceNotFound(OverrideFile)));

        // Act
        RunResult run = await PatchAsync(builder);

        // Assert
        run.Status.ShouldBe(
            $"Nie znaleziono pliku {OverrideFile}, wybranego w „Ustawieniach”. Podłącz dysk z tym plikiem albo wyczyść pole " +
            "„Własny plik polish.txt” w „Ustawieniach”, a program pobierze spolszczenie z serwera.");
        run.Percent.ShouldBe(0);
        await builder.SyncHandler.DidNotReceive().Handle(Arg.Any<SyncTranslationFileCommand>(), Arg.Any<CancellationToken>());
        await builder.PatchHandler.DidNotReceive().Handle(Arg.Any<PatchIfNeededCommand>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(TranslationFileSyncOutcome.OfflineUsedCache)]
    [InlineData(TranslationFileSyncOutcome.IntegrityCheckFailedUsedCache)]
    [InlineData(TranslationFileSyncOutcome.EndpointUnresolvedUsedCache)]
    public async Task PatchAsync_DownloadFailedAndNoEarlierDownload_SaysSoAndPatchesNothing(TranslationFileSyncOutcome outcome)
    {
        // Arrange
        ViewModelBuilder builder = SavedGameUsingTheDownload();
        builder.SyncHandler.Handle(Arg.Any<SyncTranslationFileCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new TranslationFileSyncResponse(outcome, "the server could not be reached")));
        builder.Workspace.UseTranslationFile(ViewModelBuilder.DownloadedTranslationFilePath)
            .Returns(Result.Failure(DomainErrors.DesktopPatchWorkspace.SourceNotFound(ViewModelBuilder.DownloadedTranslationFilePath)));

        // Act
        RunResult run = await PatchAsync(builder);

        // Assert
        run.Status.ShouldBe(
            "Nie udało się pobrać spolszczenia, a na tym komputerze nie ma jeszcze pobranego pliku. " +
            "Sprawdź połączenie z internetem i adres serwera w „Ustawieniach”, a potem spróbuj ponownie.");
        await builder.PatchHandler.DidNotReceive().Handle(Arg.Any<PatchIfNeededCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PatchAsync_TheDownloadCannotBeSaved_ShowsTheGeneralTextAndPatchesNothing()
    {
        // Arrange: a full disk or a locked file in the data folder.
        ViewModelBuilder builder = SavedGameUsingTheDownload();
        builder.SyncHandler.Handle(Arg.Any<SyncTranslationFileCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<TranslationFileSyncResponse>(
                DomainErrors.TranslationFileSync.CacheWriteError(ViewModelBuilder.DownloadedTranslationFilePath, "disk full")));

        // Act
        RunResult run = await PatchAsync(builder);

        // Assert
        run.Status.ShouldBe("Coś poszło nie tak. Szczegóły są w pliku data\\LotroPoPolsku.log w folderze programu.");
        run.Percent.ShouldBe(0);
        await builder.PatchHandler.DidNotReceive().Handle(Arg.Any<PatchIfNeededCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PatchAsync_TheOverrideCannotBeCopied_SaysTheProgramFolderFailedAndPatchesNothing()
    {
        // Arrange
        ViewModelBuilder builder = SavedGameWithTheOverride();
        builder.Workspace.UseTranslationFile(OverrideFile)
            .Returns(Result.Failure(DomainErrors.DesktopPatchWorkspace.FileSystemError(ViewModelBuilder.WorkingTranslationFilePath, "denied")));

        // Act
        RunResult run = await PatchAsync(builder);

        // Assert
        run.Status.ShouldBe("Nie udało się przygotować plików spolszczenia w folderze programu. Szczegóły są w pliku data\\LotroPoPolsku.log w folderze programu.");
        run.Percent.ShouldBe(0);
        await builder.PatchHandler.DidNotReceive().Handle(Arg.Any<PatchIfNeededCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PatchAsync_TheDownloadCannotBeCopied_SaysTheProgramFolderFailedAndPatchesNothing()
    {
        // Arrange
        ViewModelBuilder builder = SavedGameUsingTheDownload();
        builder.Workspace.UseTranslationFile(ViewModelBuilder.DownloadedTranslationFilePath)
            .Returns(Result.Failure(DomainErrors.DesktopPatchWorkspace.FileSystemError(ViewModelBuilder.WorkingTranslationFilePath, "denied")));

        // Act
        RunResult run = await PatchAsync(builder);

        // Assert
        run.Status.ShouldBe("Nie udało się przygotować plików spolszczenia w folderze programu. Szczegóły są w pliku data\\LotroPoPolsku.log w folderze programu.");
        run.Percent.ShouldBe(0);
        await builder.PatchHandler.DidNotReceive().Handle(Arg.Any<PatchIfNeededCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PatchAsync_TheServerConfirmsADownloadThatIsGone_NamesTheFilesToDelete()
    {
        // Arrange
        ViewModelBuilder builder = SavedGameUsingTheDownload();
        builder.Workspace.UseTranslationFile(ViewModelBuilder.DownloadedTranslationFilePath)
            .Returns(Result.Failure(DomainErrors.DesktopPatchWorkspace.SourceNotFound(ViewModelBuilder.DownloadedTranslationFilePath)));

        // Act
        RunResult run = await PatchAsync(builder);

        // Assert
        string download = ViewModelBuilder.DownloadedTranslationFilePath;
        run.Status.ShouldBe(
            $"Brakuje pobranego pliku spolszczenia {download}. Usuń też plik {download}.etag i spróbuj ponownie, " +
            "a program pobierze spolszczenie od nowa.");
    }

    [Theory]
    [InlineData(TranslationFileSyncOutcome.OfflineUsedCache)]
    [InlineData(TranslationFileSyncOutcome.IntegrityCheckFailedUsedCache)]
    [InlineData(TranslationFileSyncOutcome.EndpointUnresolvedUsedCache)]
    public async Task PatchAsync_DownloadFailedButAnEarlierOneExists_PatchesItAndSaysSo(TranslationFileSyncOutcome outcome)
    {
        // Arrange
        ViewModelBuilder builder = SavedGameUsingTheDownload();
        builder.SyncHandler.Handle(Arg.Any<SyncTranslationFileCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new TranslationFileSyncResponse(outcome, "the server could not be used")));
        builder.PatchHandler.Handle(Arg.Any<PatchIfNeededCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new PatchIfNeededResponse(true, true, 10, 0, 0, null)));

        // Act
        RunResult run = await PatchAsync(builder);

        // Assert
        run.Status.ShouldBe("Spolszczenie nałożone. Nie udało się pobrać nowego spolszczenia, więc użyto pobranego wcześniej.");
    }

    public static TheoryData<Error, string> PatchFailures() => new()
    {
        { DomainErrors.GameLaunch.GameAlreadyRunning, "Gra albo jej launcher już działa. Zamknij je i spróbuj ponownie." },
        { DomainErrors.GameLaunch.RepatchFailed("CannotOpen"), "Nie udało się nałożyć spolszczenia. Szczegóły są w pliku data\\LotroPoPolsku.log w folderze programu." },
        {
            DomainErrors.DesktopPatchWorkspace.FileSystemError("data", "denied"),
            "Nie udało się przygotować plików spolszczenia w folderze programu. Szczegóły są w pliku data\\LotroPoPolsku.log w folderze programu."
        },
        { Error.Failure("GameUpdateCheck.VersionFileError", "unreadable"), "Coś poszło nie tak. Szczegóły są w pliku data\\LotroPoPolsku.log w folderze programu." }
    };

    [Theory]
    [MemberData(nameof(PatchFailures))]
    public async Task PatchAsync_APatchError_ShowsThePolishTextForItsCodeAndResetsTheBar(Error error, string expectedStatus)
    {
        // Arrange: the failure comes after the patcher moved the bar (a version file that cannot be
        // saved fails after the whole patch), so the 0 at the end is the reset, not the start.
        ViewModelBuilder builder = SavedGameUsingTheDownload();
        builder.PatchHandler.Handle(Arg.Any<PatchIfNeededCommand>(), Arg.Any<CancellationToken>())
            .Returns(_ => ReportThenAnswer(builder, Result.Failure<PatchIfNeededResponse>(error)));

        // Act
        RunResult run = await PatchAsync(builder);

        // Assert
        run.Status.ShouldBe(expectedStatus);
        run.Percent.ShouldBe(0);
        run.GateBusy.ShouldBeFalse();
    }

    [Fact]
    public async Task PatchAsync_AHandlerThrows_ShowsTheGeneralTextFreesTheGateAndCanRunAgain()
    {
        // Arrange
        ViewModelBuilder builder = SavedGameUsingTheDownload();
        builder.PatchHandler.Handle(Arg.Any<PatchIfNeededCommand>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask<Result<PatchIfNeededResponse>>>(_ => throw new InvalidOperationException("a bug"));

        // Act
        RunResult run = await PatchAsync(builder);

        // Assert
        run.Status.ShouldBe("Coś poszło nie tak. Szczegóły są w pliku data\\LotroPoPolsku.log w folderze programu.");
        run.Percent.ShouldBe(0);
        run.GateBusy.ShouldBeFalse();
        run.CanStartAgain.ShouldBeTrue();
    }

    [Fact]
    public async Task PatchAsync_TheGameFileCheckFails_StopsBeforeThePatch()
    {
        // Arrange: the check of which game file was patched runs between the copy and the patch.
        ViewModelBuilder builder = SavedGameUsingTheDownload();
        builder.Workspace.ResetPatchStateIfGameFileChanged(ViewModelBuilder.SavedDatFilePath)
            .Returns(Result.Failure(DomainErrors.DesktopPatchWorkspace.FileSystemError("data", "denied")));

        // Act
        RunResult run = await PatchAsync(builder);

        // Assert
        run.Status.ShouldBe(
            "Nie udało się przygotować plików spolszczenia w folderze programu. Szczegóły są w pliku data\\LotroPoPolsku.log w folderze programu.");
        await builder.PatchHandler.DidNotReceive().Handle(Arg.Any<PatchIfNeededCommand>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_TheSavedFolderLostItsGameFile_SaysSoAndTouchesNothing(bool play)
    {
        // Arrange
        ViewModelBuilder builder = new();
        builder.SettingsFile.Contents = ViewModelBuilder.SettingsJson(gameFolder: ViewModelBuilder.SavedGameFolder);

        // Act
        RunResult run = play ? await PlayAsync(builder) : await PatchAsync(builder);

        // Assert
        run.Status.ShouldBe("Nie znaleziono pliku gry w wybranym folderze. Sprawdź folder gry w „Ustawieniach”.");
        run.Percent.ShouldBe(0);
        await builder.SyncHandler.DidNotReceive().Handle(Arg.Any<SyncTranslationFileCommand>(), Arg.Any<CancellationToken>());
        await builder.LaunchHandler.DidNotReceive().Handle(Arg.Any<GameLaunchingCommand>(), Arg.Any<CancellationToken>());
        builder.Launcher.DidNotReceive().Launch(Arg.Any<string>());
    }

    [Fact]
    public async Task PatchAsync_APortThrows_ShowsTheGeneralTextFreesTheGateAndPatchesNothing()
    {
        // Arrange: a hand-edited override path with a character no file system accepts throws in the copy.
        ViewModelBuilder builder = SavedGameWithTheOverride();
        builder.Workspace.UseTranslationFile(OverrideFile).Returns(_ => throw new ArgumentException("Null character in path."));

        // Act
        RunResult run = await PatchAsync(builder);

        // Assert
        run.Status.ShouldBe("Coś poszło nie tak. Szczegóły są w pliku data\\LotroPoPolsku.log w folderze programu.");
        run.Percent.ShouldBe(0);
        run.GateBusy.ShouldBeFalse();
        run.CanStartAgain.ShouldBeTrue();
        await builder.PatchHandler.DidNotReceive().Handle(Arg.Any<PatchIfNeededCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PatchAsync_WhileAnotherOperationHoldsTheGate_RunsNothingAndKeepsTheButtonsDisabled()
    {
        // Arrange: an update holds the gate from "Tak" until the app closes.
        ViewModelBuilder builder = SavedGameUsingTheDownload();
        builder.Gate.TryEnter();

        // Act
        RunResult run = await PatchAsync(builder);

        // Assert
        run.Status.ShouldBe("Gotowe.");
        run.CanStartAgain.ShouldBeFalse();
        await builder.SyncHandler.DidNotReceive().Handle(Arg.Any<SyncTranslationFileCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Commands_WithNoGameFolderSaved_AreDisabled()
    {
        bool canStart = await _fixture.Session.Dispatch(
            () =>
            {
                ViewModelBuilder builder = new();
                builder.Store.Load();
                PatchAndPlayViewModel viewModel = builder.PatchAndPlay(builder.GameFolder());
                return viewModel.PatchCommand.CanExecute(null) || viewModel.PlayCommand.CanExecute(null);
            },
            CancellationToken.None);

        canStart.ShouldBeFalse();
    }

    [Fact]
    public async Task Commands_WhileTheFirstRunPanelAsks_AreDisabled()
    {
        (bool PanelVisible, bool CanStart) state = await _fixture.Session.Dispatch(
            async () =>
            {
                ViewModelBuilder builder = new();
                builder.SettingsFile.Contents = ViewModelBuilder.SettingsJson(gameFolder: ViewModelBuilder.SavedGameFolder);
                GameFolderViewModel gameFolder = builder.GameFolder();
                PatchAndPlayViewModel viewModel = builder.PatchAndPlay(gameFolder);
                await gameFolder.StartAsync();
                return (gameFolder.IsPanelVisible, viewModel.PatchCommand.CanExecute(null) || viewModel.PlayCommand.CanExecute(null));
            },
            CancellationToken.None);

        // The saved folder lost its game file, so the panel asks for another one.
        state.PanelVisible.ShouldBeTrue();
        state.CanStart.ShouldBeFalse();
    }

    [Fact]
    public async Task PlayAsync_AutoPatchOn_RunsTheSettledLaunchOnTheWorkingCopy()
    {
        // Arrange
        ViewModelBuilder builder = SavedGameUsingTheDownload();
        builder.LaunchHandler.Handle(Arg.Any<GameLaunchingCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<GameLaunchingResponse>(Error.Failure("Test.WrongCommand", "not the expected paths")));
        builder.LaunchHandler.Handle(
                new GameLaunchingCommand(
                    ViewModelBuilder.SavedDatFilePath, ViewModelBuilder.GameVersionFilePath, ViewModelBuilder.WorkingTranslationFilePath),
                Arg.Any<CancellationToken>())
            .Returns(Result.Success(new GameLaunchingResponse(null, false, 0, TranslationsApplied: true, AppliedCount: 10)));

        // Act
        RunResult run = await PlayAsync(builder);

        // Assert
        run.Status.ShouldBe("Spolszczenie nałożone. Uruchomiono launcher gry.");
        run.Percent.ShouldBe(100);
        run.IsIndeterminate.ShouldBeFalse();
        run.GateBusy.ShouldBeFalse();
    }

    public static TheoryData<GameLaunchingResponse, string> LaunchOutcomes() => new()
    {
        { new GameLaunchingResponse(null, false, 0), "Uruchomiono launcher gry." },
        {
            new GameLaunchingResponse(null, false, 0, TranslationsApplied: true, AppliedCount: 0, SkippedCount: 1200),
            "Uruchomiono launcher gry, ale z pliku spolszczenia nie nałożono żadnego tekstu. Szczegóły są w pliku data\\LotroPoPolsku.log w folderze programu."
        },
        {
            new GameLaunchingResponse(null, false, 0, WipedSubFileCount: 2, RepairFailure: "CannotOpen"),
            "Uruchomiono launcher gry, ale część tekstów będzie po angielsku: nie udało się ponownie nałożyć spolszczenia. " +
            "Szczegóły są w pliku data\\LotroPoPolsku.log w folderze programu."
        }
    };

    [Theory]
    [MemberData(nameof(LaunchOutcomes))]
    public async Task PlayAsync_EachLaunchOutcome_ShowsItsPolishText(GameLaunchingResponse response, string expectedStatus)
    {
        // Arrange
        ViewModelBuilder builder = SavedGameUsingTheDownload();
        builder.LaunchHandler.Handle(Arg.Any<GameLaunchingCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Success(response));

        // Act
        RunResult run = await PlayAsync(builder);

        // Assert
        run.Status.ShouldBe(expectedStatus);
    }

    public static TheoryData<Error, string> LaunchFailures() => new()
    {
        {
            DomainErrors.GameLaunch.LauncherNotFound(ViewModelBuilder.SavedGameFolder),
            "Nie znaleziono pliku LotroLauncher.exe w folderze gry. Sprawdź folder gry w „Ustawieniach”."
        },
        { DomainErrors.GameLaunch.LaunchFailed("The system cannot execute the specified program."), "Nie udało się uruchomić launchera gry. Szczegóły są w pliku data\\LotroPoPolsku.log w folderze programu." },
        { DomainErrors.GameLaunch.GameAlreadyRunning, "Gra albo jej launcher już działa. Zamknij je i spróbuj ponownie." },
        { DomainErrors.GameLaunch.RepatchFailed("CannotOpen"), "Nie udało się nałożyć spolszczenia. Szczegóły są w pliku data\\LotroPoPolsku.log w folderze programu." }
    };

    [Theory]
    [MemberData(nameof(LaunchFailures))]
    public async Task PlayAsync_ALaunchError_ShowsThePolishTextForItsCodeAndResetsTheBar(Error error, string expectedStatus)
    {
        // Arrange
        ViewModelBuilder builder = SavedGameUsingTheDownload();
        builder.LaunchHandler.Handle(Arg.Any<GameLaunchingCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<GameLaunchingResponse>(error));

        // Act
        RunResult run = await PlayAsync(builder);

        // Assert
        run.Status.ShouldBe(expectedStatus);
        run.Percent.ShouldBe(0);
        run.GateBusy.ShouldBeFalse();
    }

    [Theory]
    [InlineData(TranslationFileSyncOutcome.OfflineUsedCache)]
    [InlineData(TranslationFileSyncOutcome.IntegrityCheckFailedUsedCache)]
    [InlineData(TranslationFileSyncOutcome.EndpointUnresolvedUsedCache)]
    public async Task PlayAsync_DownloadFailedButAnEarlierOneExists_LaunchesWithItAndSaysSo(TranslationFileSyncOutcome outcome)
    {
        // Arrange
        ViewModelBuilder builder = SavedGameUsingTheDownload();
        builder.SyncHandler.Handle(Arg.Any<SyncTranslationFileCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new TranslationFileSyncResponse(outcome, "the server could not be used")));

        // Act
        RunResult run = await PlayAsync(builder);

        // Assert
        run.Status.ShouldBe("Uruchomiono launcher gry. Nie udało się pobrać nowego spolszczenia, więc użyto pobranego wcześniej.");
    }

    [Fact]
    public async Task PlayAsync_AutoPatchOff_OnlyStartsTheLauncher()
    {
        // Arrange
        ViewModelBuilder builder = SavedGame(autoPatch: false);

        // Act
        RunResult run = await PlayAsync(builder);

        // Assert: no download, no copy and no patch; starting the launcher is the run's only effect.
        run.Status.ShouldBe("Uruchomiono launcher gry.");
        run.Percent.ShouldBe(100);
        builder.Launcher.Received(1).Launch(ViewModelBuilder.SavedDatFilePath);
        await builder.SyncHandler.DidNotReceive().Handle(Arg.Any<SyncTranslationFileCommand>(), Arg.Any<CancellationToken>());
        builder.Workspace.DidNotReceive().UseTranslationFile(Arg.Any<string>());
        await builder.LaunchHandler.DidNotReceive().Handle(Arg.Any<GameLaunchingCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PlayAsync_AutoPatchOffWhileTheLauncherStarts_SaysSoWithAMovingBar()
    {
        // Arrange
        ViewModelBuilder builder = SavedGame(autoPatch: false);
        using ManualResetEventSlim release = new();
        builder.Launcher.Launch(ViewModelBuilder.SavedDatFilePath).Returns(_ =>
        {
            release.Wait(TimeSpan.FromSeconds(10));
            return Result.Success();
        });

        (string Status, bool Indeterminate, string StatusAfter) play = await _fixture.Session.Dispatch(
            async () =>
            {
                PatchAndPlayViewModel viewModel = Create(builder);

                // Act
                Task run = viewModel.PlayAsync();
                Dispatcher.UIThread.RunJobs();
                (string Status, bool Indeterminate) during = (viewModel.StatusText, builder.Progress.IsIndeterminate);

                release.Set();
                await run;
                return (during.Status, during.Indeterminate, viewModel.StatusText);
            },
            CancellationToken.None);

        // Assert
        play.Status.ShouldBe("Uruchamiam grę…");
        play.Indeterminate.ShouldBeTrue();
        play.StatusAfter.ShouldBe("Uruchomiono launcher gry.");
    }

    [Fact]
    public async Task PlayAsync_AutoPatchOffWithAMissingOverride_StillStartsTheGame()
    {
        // Arrange: with auto-patch off, Play uses no translation file, so a gone override cannot stop it.
        ViewModelBuilder builder = SavedGame(autoPatch: false, translationFile: OverrideFile);
        builder.Workspace.UseTranslationFile(OverrideFile)
            .Returns(Result.Failure(DomainErrors.DesktopPatchWorkspace.SourceNotFound(OverrideFile)));

        // Act
        RunResult run = await PlayAsync(builder);

        // Assert
        run.Status.ShouldBe("Uruchomiono launcher gry.");
    }

    [Fact]
    public async Task PlayAsync_AutoPatchOffWhileTheGameRuns_RefusesAndStartsNothing()
    {
        // Arrange
        ViewModelBuilder builder = SavedGame(autoPatch: false);
        builder.ProcessDetector.IsLotroRunning().Returns(true);

        // Act
        RunResult run = await PlayAsync(builder);

        // Assert
        run.Status.ShouldBe("Gra albo jej launcher już działa. Zamknij je i spróbuj ponownie.");
        run.Percent.ShouldBe(0);
        builder.Launcher.DidNotReceive().Launch(Arg.Any<string>());
    }

    [Fact]
    public async Task PlayAsync_AutoPatchOnWithAMissingOverride_StopsWithThePolishMessage()
    {
        // Arrange
        ViewModelBuilder builder = SavedGameWithTheOverride();
        builder.Workspace.UseTranslationFile(OverrideFile)
            .Returns(Result.Failure(DomainErrors.DesktopPatchWorkspace.SourceNotFound(OverrideFile)));

        // Act
        RunResult run = await PlayAsync(builder);

        // Assert
        run.Status.ShouldStartWith($"Nie znaleziono pliku {OverrideFile}, wybranego w „Ustawieniach”.");
        await builder.LaunchHandler.DidNotReceive().Handle(Arg.Any<GameLaunchingCommand>(), Arg.Any<CancellationToken>());
        await builder.SyncHandler.DidNotReceive().Handle(Arg.Any<SyncTranslationFileCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefuseClose_DuringARun_RefusesSaysWhyAndHoldsTheGateUntilTheEnd()
    {
        // Arrange
        ViewModelBuilder builder = SavedGameUsingTheDownload();
        TaskCompletionSource<Result<PatchIfNeededResponse>> patch = new(TaskCreationOptions.RunContinuationsAsynchronously);
        builder.PatchHandler.Handle(Arg.Any<PatchIfNeededCommand>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask<Result<PatchIfNeededResponse>>(patch.Task));

        (bool Refused, string Status, bool GateBusy, bool CanStart, bool RefusedAfter) close = await _fixture.Session.Dispatch(
            async () =>
            {
                PatchAndPlayViewModel viewModel = Create(builder);
                Task run = viewModel.PatchAsync();
                await WaitUntil(() => viewModel.StatusText == "Nakładam spolszczenie…");

                // Act
                bool refused = viewModel.RefuseClose();
                (string Status, bool GateBusy, bool CanStart) during =
                    (viewModel.StatusText, builder.Gate.IsBusy, viewModel.PatchCommand.CanExecute(null));

                patch.SetResult(Result.Success(new PatchIfNeededResponse(false, false, 0, 0, 0, null)));
                await run;
                return (refused, during.Status, during.GateBusy, during.CanStart, viewModel.RefuseClose());
            },
            CancellationToken.None);

        // Assert
        close.Refused.ShouldBeTrue();
        close.Status.ShouldBe("Poczekaj, aż ta operacja się skończy. Potem zamknij okno.");
        close.GateBusy.ShouldBeTrue();
        close.CanStart.ShouldBeFalse();
        close.RefusedAfter.ShouldBeFalse();
    }

    [Fact]
    public async Task RefuseClose_DuringAnUpdate_AllowsTheClose()
    {
        bool refused = await _fixture.Session.Dispatch(
            () =>
            {
                ViewModelBuilder builder = SavedGameUsingTheDownload();
                PatchAndPlayViewModel viewModel = Create(builder);
                builder.Gate.TryEnter();
                return viewModel.RefuseClose();
            },
            CancellationToken.None);

        refused.ShouldBeFalse();
    }

    public PatchAndPlayViewModelTests(HeadlessSessionFixture fixture)
    {
        _fixture = fixture;
    }

    private static ViewModelBuilder SavedGame(bool autoPatch = true, string? translationFile = null)
    {
        ViewModelBuilder builder = new();
        builder.Files.AddGame(ViewModelBuilder.SavedGameFolder);
        builder.SettingsFile.Contents = ViewModelBuilder.SettingsJson(
            gameFolder: ViewModelBuilder.SavedGameFolder,
            translationFile: translationFile,
            autoPatch: autoPatch,
            serverAddress: Server);
        return builder;
    }

    private static ViewModelBuilder SavedGameUsingTheDownload() => SavedGame();

    private static ViewModelBuilder SavedGameWithTheOverride() => SavedGame(translationFile: OverrideFile);

    private static PatchAndPlayViewModel Create(ViewModelBuilder builder)
    {
        builder.Store.Load();
        return builder.PatchAndPlay(builder.GameFolder());
    }

    /// <summary>The patcher reports every 1000 rows from its own thread; this stands in for one report.</summary>
    private static ValueTask<Result<PatchIfNeededResponse>> ReportThenAnswer(
        ViewModelBuilder builder, Result<PatchIfNeededResponse> answer)
    {
        builder.Progress.Report(new OperationProgress(600, 1000));
        return new ValueTask<Result<PatchIfNeededResponse>>(answer);
    }

    private Task<RunResult> PatchAsync(ViewModelBuilder builder) => RunAsync(builder, viewModel => viewModel.PatchAsync());

    private Task<RunResult> PlayAsync(ViewModelBuilder builder) => RunAsync(builder, viewModel => viewModel.PlayAsync());

    private Task<RunResult> RunAsync(ViewModelBuilder builder, Func<PatchAndPlayViewModel, Task> run) =>
        _fixture.Session.Dispatch(
            async () =>
            {
                PatchAndPlayViewModel viewModel = Create(builder);
                await run(viewModel);
                Dispatcher.UIThread.RunJobs();
                return new RunResult(
                    viewModel.StatusText,
                    builder.Progress.Percent,
                    builder.Progress.IsIndeterminate,
                    builder.Gate.IsBusy,
                    viewModel.PatchCommand.CanExecute(null));
            },
            CancellationToken.None);

    private static async Task WaitUntil(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20));
            Dispatcher.UIThread.RunJobs();
        }
    }

    private sealed record RunResult(string Status, int Percent, bool IsIndeterminate, bool GateBusy, bool CanStartAgain);
}
