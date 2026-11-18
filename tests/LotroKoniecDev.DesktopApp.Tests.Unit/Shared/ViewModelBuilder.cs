using System.Text.Json;
using LotroKoniecDev.Application;
using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Abstractions.DatFilesServices;
using LotroKoniecDev.Application.Abstractions.Messaging;
using LotroKoniecDev.Application.Features.AppUpdating;
using LotroKoniecDev.Application.Features.GameLaunching;
using LotroKoniecDev.Application.Features.Patching;
using LotroKoniecDev.Application.Features.TranslationFileSyncing;
using LotroKoniecDev.DesktopApp.Presentation;
using LotroKoniecDev.DesktopApp.Presentation.Settings;
using LotroKoniecDev.DesktopApp.Presentation.ViewModels;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Domain.Models;
using LotroKoniecDev.Primitives.Constants;
using LotroKoniecDev.Primitives.Enums;
using Microsoft.Extensions.Logging.Abstractions;

namespace LotroKoniecDev.DesktopApp.Tests.Unit.Shared;

/// <summary>
/// The view models over in-memory settings, files and dialogs, with their handlers stubbed.
/// <see cref="WithASavedGame"/> is the default for window tests: a saved folder that holds the game, so
/// an opened window shows no first-run panel and starts no search. By default the update check answers
/// "developer build", so an opened window shows no update panel either. Patch and Play succeed by
/// default: the download is current, the patch finds nothing to do and the launcher starts. Every view
/// model one builder makes shares its busy gate and its progress bar, as in the app.
/// </summary>
internal sealed class ViewModelBuilder
{
    public static readonly string SavedGameFolder = Rooted("Games", "LOTRO");

    public static readonly string SavedDatFilePath = Path.Combine(SavedGameFolder, DatFileConstants.DatFileName);

    public static readonly string DownloadedTranslationFilePath = Rooted("App", "data", "download", "polish.txt");

    public static readonly string WorkingTranslationFilePath = Rooted("App", "data", "polish.txt");

    public static readonly string GameVersionFilePath = Rooted("App", "data", "last_known_game_version.txt");

    public InMemorySettingsFile SettingsFile { get; } = new();

    public InMemoryFileProbe Files { get; } = new();

    public IDatFileLocator Locator { get; } = Substitute.For<IDatFileLocator>();

    public IPathPicker Picker { get; } = Substitute.For<IPathPicker>();

    public OperationGate Gate { get; } = new();

    public ProgressBarViewModel Progress { get; } = new();

    public ICommandHandler<SyncTranslationFileCommand, Result<TranslationFileSyncResponse>> SyncHandler { get; } =
        Substitute.For<ICommandHandler<SyncTranslationFileCommand, Result<TranslationFileSyncResponse>>>();

    public ICommandHandler<PatchIfNeededCommand, Result<PatchIfNeededResponse>> PatchHandler { get; } =
        Substitute.For<ICommandHandler<PatchIfNeededCommand, Result<PatchIfNeededResponse>>>();

    public ICommandHandler<GameLaunchingCommand, Result<GameLaunchingResponse>> LaunchHandler { get; } =
        Substitute.For<ICommandHandler<GameLaunchingCommand, Result<GameLaunchingResponse>>>();

    public IDesktopPatchWorkspace Workspace { get; } = Substitute.For<IDesktopPatchWorkspace>();

    public IGameProcessDetector ProcessDetector { get; } = Substitute.For<IGameProcessDetector>();

    public IGameLauncher Launcher { get; } = Substitute.For<IGameLauncher>();

    public DesktopSettingsStore Store =>
        field ??= new DesktopSettingsStore(SettingsFile, Files, NullLogger<DesktopSettingsStore>.Instance);

    public static string Rooted(params string[] parts) =>
        Path.Combine([Path.GetPathRoot(Path.GetTempPath())!, .. parts]);

    public static string SettingsJson(
        string? gameFolder = null,
        string? translationFile = null,
        bool? autoPatch = null,
        string? serverAddress = null)
    {
        Dictionary<string, object?> document = new()
        {
            ["gameFolder"] = gameFolder,
            ["translationFile"] = translationFile,
            ["autoPatch"] = autoPatch,
            ["serverAddress"] = serverAddress
        };
        return JsonSerializer.Serialize(document);
    }

    public static DatFileLocation Location(string folder, DatFileSource source) =>
        new(Path.Combine(folder, DatFileConstants.DatFileName), source, "an English name the player never sees");

    /// <summary>Each location's folder holds the game, as the real locator only reports files it found.</summary>
    public ViewModelBuilder Locating(params DatFileLocation[] locations)
    {
        foreach (DatFileLocation location in locations)
        {
            Files.Add(location.Path);
        }

        Locator.LocateAll(Arg.Any<Action<string>?>()).Returns(Result.Success<IReadOnlyList<DatFileLocation>>(locations));
        return this;
    }

    public ViewModelBuilder PickingFolder(string? folder)
    {
        Picker.PickGameFolderAsync().Returns(folder);
        return this;
    }

    public GameFolderViewModel GameFolder() =>
        new(Locator, Store, Picker, Gate, NullLogger<GameFolderViewModel>.Instance);

    public SettingsViewModel Settings() =>
        new(Store, Picker, Gate, NullLogger<SettingsViewModel>.Instance);

    public PatchAndPlayViewModel PatchAndPlay(GameFolderViewModel gameFolder) =>
        new(
            SyncHandler,
            PatchHandler,
            LaunchHandler,
            Workspace,
            ProcessDetector,
            Launcher,
            Store,
            gameFolder,
            Gate,
            Progress,
            NullLogger<PatchAndPlayViewModel>.Instance);

    public MainWindowViewModel MainWindow(AppUpdateViewModel? appUpdate = null)
    {
        GameFolderViewModel gameFolder = GameFolder();
        return new MainWindowViewModel(
            Progress,
            gameFolder,
            Settings(),
            appUpdate ?? AppUpdate(DevelopmentBuildCheck(), gate: Gate, progress: Progress),
            PatchAndPlay(gameFolder));
    }

    public static AppUpdateViewModel AppUpdate(
        IQueryHandler<CheckForAppUpdateQuery, Result<AppUpdateCheckResponse>> checkHandler,
        ICommandHandler<InstallAppUpdateCommand, Result>? installHandler = null,
        OperationGate? gate = null,
        string installedVersion = "0.1.0",
        IAppShutdown? appShutdown = null,
        IProgress<OperationProgress>? progress = null) =>
        new(
            checkHandler,
            installHandler ?? Substitute.For<ICommandHandler<InstallAppUpdateCommand, Result>>(),
            gate ?? new OperationGate(),
            new InstalledAppVersion(installedVersion),
            appShutdown ?? Substitute.For<IAppShutdown>(),
            progress ?? new ProgressBarViewModel(),
            NullLogger<AppUpdateViewModel>.Instance);

    public static IQueryHandler<CheckForAppUpdateQuery, Result<AppUpdateCheckResponse>> CheckAnswering(
        AppUpdateCheckResponse response)
    {
        IQueryHandler<CheckForAppUpdateQuery, Result<AppUpdateCheckResponse>> handler =
            Substitute.For<IQueryHandler<CheckForAppUpdateQuery, Result<AppUpdateCheckResponse>>>();
        handler.Handle(Arg.Any<CheckForAppUpdateQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(response));
        return handler;
    }

    /// <summary>A check that answers only when the test completes <paramref name="answer"/>.</summary>
    public static IQueryHandler<CheckForAppUpdateQuery, Result<AppUpdateCheckResponse>> CheckAnsweringWhen(
        Task<Result<AppUpdateCheckResponse>> answer)
    {
        IQueryHandler<CheckForAppUpdateQuery, Result<AppUpdateCheckResponse>> handler =
            Substitute.For<IQueryHandler<CheckForAppUpdateQuery, Result<AppUpdateCheckResponse>>>();
        handler.Handle(Arg.Any<CheckForAppUpdateQuery>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask<Result<AppUpdateCheckResponse>>(answer));
        return handler;
    }

    public static IQueryHandler<CheckForAppUpdateQuery, Result<AppUpdateCheckResponse>> DevelopmentBuildCheck() =>
        CheckAnswering(AppUpdateCheckResponse.DevelopmentBuild());

    public static ViewModelBuilder WithASavedGame()
    {
        ViewModelBuilder builder = new();
        builder.Files.AddGame(SavedGameFolder);
        builder.SettingsFile.Contents = SettingsJson(gameFolder: SavedGameFolder);
        return builder;
    }

    public ViewModelBuilder()
    {
        Locator.LocateAll(Arg.Any<Action<string>?>())
            .Returns(Result.Failure<IReadOnlyList<DatFileLocation>>(DomainErrors.DatFileLocation.NoneFound));

        Workspace.DownloadedTranslationFilePath.Returns(DownloadedTranslationFilePath);
        Workspace.WorkingTranslationFilePath.Returns(WorkingTranslationFilePath);
        Workspace.GameVersionFilePath.Returns(GameVersionFilePath);
        Workspace.UseTranslationFile(Arg.Any<string>()).Returns(Result.Success());
        Workspace.ResetPatchStateIfGameFileChanged(Arg.Any<string>()).Returns(Result.Success());
        SyncHandler.Handle(Arg.Any<SyncTranslationFileCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new TranslationFileSyncResponse(TranslationFileSyncOutcome.UpToDate, null)));
        PatchHandler.Handle(Arg.Any<PatchIfNeededCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new PatchIfNeededResponse(false, false, 0, 0, 0, null)));
        LaunchHandler.Handle(Arg.Any<GameLaunchingCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new GameLaunchingResponse(null, false, 0)));
        Launcher.Launch(Arg.Any<string>()).Returns(Result.Success());
    }
}
