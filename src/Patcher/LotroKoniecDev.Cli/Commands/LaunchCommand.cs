using System.ComponentModel;
using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Abstractions.DatFilesServices;
using LotroKoniecDev.Application.Abstractions.Messaging;
using LotroKoniecDev.Application.Features.GameLaunching;
using LotroKoniecDev.Application.Features.Patching;
using LotroKoniecDev.Application.Features.PreflightChecking;
using LotroKoniecDev.Application.Features.TranslationFileSyncing;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Domain.Models;
using Spectre.Console.Cli;

namespace LotroKoniecDev.Cli.Commands;

internal sealed class LaunchCommand : AsyncCommand<LaunchCommand.Settings>
{
    private readonly ICommandHandler<GameLaunchingCommand, Result<GameLaunchingResponse>> _gameLaunchingHandler;
    private readonly ICommandHandler<SyncTranslationFileCommand, Result<TranslationFileSyncResponse>> _translationFileSyncHandler;
    private readonly IFileProvider _fileProvider;
    private readonly IOperationStatusReporter _reporter;
    private readonly IDatPathResolver _datPathResolver;

    public LaunchCommand(
        ICommandHandler<GameLaunchingCommand, Result<GameLaunchingResponse>> gameLaunchingHandler,
        ICommandHandler<SyncTranslationFileCommand, Result<TranslationFileSyncResponse>> translationFileSyncHandler,
        IFileProvider fileProvider,
        IOperationStatusReporter reporter,
        IDatPathResolver datPathResolver)
    {
        _gameLaunchingHandler = gameLaunchingHandler;
        _translationFileSyncHandler = translationFileSyncHandler;
        _fileProvider = fileProvider;
        _reporter = reporter;
        _datPathResolver = datPathResolver;
    }

    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "<NAME>")]
        [Description("Name resolves to translations/<name>.txt")]
        public string TranslationName { get; init; } = string.Empty;

        [CommandOption("-d|--dat-file-path")]
        [Description("Optional path to the DAT file")]
        public string? DatFilePath { get; init; }

        [CommandOption("--tms-url")]
        [Description("TMS API base URL for translation auto-download (overrides the configured default)")]
        public string TmsBaseUrl { get; init; } = DefaultTmsBaseUrl;

        [CommandOption("--skip-sync")]
        [Description("Skip auto-downloading the translation file from the TMS (offline use)")]
        [DefaultValue(false)]
        public bool SkipSync { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings,
        CancellationToken cancellationToken)
    {
        // Auto-download the current translation file before launch (spec 0001 Q5, freeze exception):
        // skipped when disabled or no TMS URL is configured. Never blocks launch on the network — an
        // offline server with a cached file proceeds; only "offline + nothing cached" aborts here.
        if (!settings.SkipSync && !string.IsNullOrWhiteSpace(settings.TmsBaseUrl))
        {
            int? syncFailureExitCode = await SyncTranslationFileAsync(settings, cancellationToken);
            if (syncFailureExitCode is not null)
            {
                return syncFailureExitCode.Value;
            }
        }

        (string TranslationsPath, string DatFilePath)? actualResolvedPaths = ResolveCommandPaths(settings);
        if (actualResolvedPaths is null)
        {
            return ExitCodes.FileNotFound;
        }

        try
        {
            GameLaunchingCommand gameLaunchingCommand = new(
                DatFilePath: actualResolvedPaths.Value.DatFilePath,
                GameVersionFilePath: GlobalSettings.VersionFilePath,
                TranslationFilePath: actualResolvedPaths.Value.TranslationsPath);

            Result<GameLaunchingResponse> result = await _gameLaunchingHandler.Handle(gameLaunchingCommand, cancellationToken);
            if (result.IsFailure)
            {
                _reporter.Report(result.Error.ToString());
                return ErrorMapper.MapErrorToExitCode(result.Error);
            }

            _reporter.Report(result.Value.ToString());
            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            _reporter.Report(ex.ToString());
            return ExitCodes.OperationFailed;
        }
    }

    private async Task<int?> SyncTranslationFileAsync(Settings settings, CancellationToken cancellationToken)
    {
        string translationsPath = ResolveTranslationsPath(settings.TranslationName);
        SyncTranslationFileCommand command = new(settings.TmsBaseUrl, translationsPath);

        Result<TranslationFileSyncResponse> result = await _translationFileSyncHandler.Handle(command, cancellationToken);
        if (result.IsFailure)
        {
            _reporter.Report(result.Error.ToString());
            return ErrorMapper.MapErrorToExitCode(result.Error);
        }

        _reporter.Report(result.Value.ToString());
        return null;
    }

    private (string TranslationsPath, string DatFilePath)? ResolveCommandPaths(Settings settings)
    {
        string actualTranslationsPath = ResolveTranslationsPath(settings.TranslationName);
        if (!_fileProvider.Exists(actualTranslationsPath))
        {
            _reporter.Report($"Translation file not found: {actualTranslationsPath}");
            return null;
        }

        string? datFilePath = _datPathResolver.Resolve(settings.DatFilePath);
        if (datFilePath is null)
        {
            return null;
        }

        if (_fileProvider.Exists(datFilePath))
        {
            return (actualTranslationsPath, datFilePath);
        }

        _reporter.Report($"DAT file not found: {datFilePath}");
        return null;
    }

    private static string ResolveTranslationsPath(string input)
    {
        return input.Contains(Path.DirectorySeparatorChar) ||
               input.Contains(Path.AltDirectorySeparatorChar) ||
               input.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
            ? input
            : Path.Combine(GlobalSettings.TranslationsDir, input + ".txt");
    }
}
