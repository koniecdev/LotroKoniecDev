using System.Text.Json;
using LotroKoniecDev.Application;
using LotroKoniecDev.Application.Abstractions.DatFilesServices;
using LotroKoniecDev.Application.Abstractions.Messaging;
using LotroKoniecDev.Application.Features.AppUpdating;
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
/// "developer build", so an opened window shows no update panel either.
/// </summary>
internal sealed class ViewModelBuilder
{
    public static readonly string SavedGameFolder = Rooted("Games", "LOTRO");

    public InMemorySettingsFile SettingsFile { get; } = new();

    public InMemoryFileProbe Files { get; } = new();

    public IDatFileLocator Locator { get; } = Substitute.For<IDatFileLocator>();

    public IPathPicker Picker { get; } = Substitute.For<IPathPicker>();

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
        new(Locator, Store, Picker, NullLogger<GameFolderViewModel>.Instance);

    public SettingsViewModel Settings() =>
        new(Store, Picker, NullLogger<SettingsViewModel>.Instance);

    public MainWindowViewModel MainWindow(AppUpdateViewModel? appUpdate = null) =>
        new(new ProgressBarViewModel(), GameFolder(), Settings(), appUpdate ?? AppUpdate(DevelopmentBuildCheck()));

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
    }
}
