using System.ComponentModel;
using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Abstractions.DatFilesServices;
using LotroKoniecDev.Application.Features.Patching;
using LotroKoniecDev.Application.Features.PreflightChecking;
using LotroKoniecDev.Domain.Core.Monads;
using Mediator;
using Spectre.Console.Cli;

namespace LotroKoniecDev.Cli.Commands;

internal sealed class PatchCommand : AsyncCommand<PatchCommand.Settings>
{
    private readonly ISender _sender;
    private readonly IBackupManager _backupManager;
    private readonly IFileProvider _fileProvider;
    private readonly IOperationStatusReporter _reporter;
    private readonly IDatPathResolver _datPathResolver;
    private readonly IVersionBaselineService _versionBaselineService;

    public PatchCommand(
        ISender sender,
        IBackupManager backupManager,
        IFileProvider fileProvider,
        IOperationStatusReporter reporter,
        IDatPathResolver datPathResolver,
        IVersionBaselineService versionBaselineService)
    {
        _sender = sender;
        _backupManager = backupManager;
        _fileProvider = fileProvider;
        _reporter = reporter;
        _datPathResolver = datPathResolver;
        _versionBaselineService = versionBaselineService;
    }

    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "<NAME>")]
        [Description("Name resolves to translations/<name>.txt")]
        public string TranslationName { get; init; } = string.Empty;

        [CommandOption("-d|--dat-file-path")]
        [Description("Optional path to the DAT file")]
        public string? DatFilePath { get; init; }
    }


    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings,
        CancellationToken cancellationToken)
    {
        (string TranslationsPath, string DatFilePath)? actualResolvedPaths = ResolveCommandPaths(settings);
        if (actualResolvedPaths is null)
        {
            return ExitCodes.FileNotFound;
        }

        PreflightCheckQuery preflightCheckQuery = new(actualResolvedPaths.Value.DatFilePath, GlobalSettings.VersionFilePath);
        Result<PreflightReportResponse> preflightCheckResponse =
            await _sender.Send(preflightCheckQuery, cancellationToken);
        if (preflightCheckResponse.IsFailure)
        {
            _reporter.Report(preflightCheckResponse.Error.ToString());
            return ErrorMapper.MapErrorToExitCode(preflightCheckResponse.Error);
        }

        Result backupResult = _backupManager.Create(actualResolvedPaths.Value.DatFilePath);
        if (backupResult.IsFailure)
        {
            _reporter.Report(backupResult.Error.ToString());
            return ErrorMapper.MapErrorToExitCode(backupResult.Error);
        }

        try
        {
            ApplyPatchCommand applyPatchCommand = new(
                TranslationsPath: actualResolvedPaths.Value.TranslationsPath,
                DatFilePath: actualResolvedPaths.Value.DatFilePath);

            Result<PatchSummaryResponse> result = await _sender.Send(applyPatchCommand, cancellationToken);
            if (result.IsFailure)
            {
                _reporter.Report(result.Error.ToString());
                return ErrorMapper.MapErrorToExitCode(result.Error);
            }

            foreach (string warning in result.Value.Warnings)
            {
                _reporter.Report(warning);
            }

            if (result.Value.SkippedTranslations > 0)
            {
                _reporter.Report($"Skipped {result.Value.SkippedTranslations} translations");
            }

            _reporter.Report(result.Value.ToString());

            // Save version baseline after successful patch so next `launch` doesn't falsely detect an update
            await _versionBaselineService.SaveBaselineAsync(
                actualResolvedPaths.Value.DatFilePath,
                actualResolvedPaths.Value.TranslationsPath,
                GlobalSettings.VersionFilePath);

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            _backupManager.Restore(actualResolvedPaths.Value.DatFilePath);
            _reporter.Report(ex.ToString());
            return ExitCodes.OperationFailed;
        }
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
