using System.ComponentModel;
using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Abstractions.DatFilesServices;
using LotroKoniecDev.Application.Features.GameLaunching;
using LotroKoniecDev.Application.Features.Patching;
using LotroKoniecDev.Application.Features.PreflightChecking;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Domain.Models;
using Mediator;
using Spectre.Console.Cli;

namespace LotroKoniecDev.Cli.Commands;

internal sealed class LaunchCommand : AsyncCommand<LaunchCommand.Settings>
{
    private readonly ISender _sender;
    private readonly IFileProvider _fileProvider;
    private readonly IOperationStatusReporter _reporter;
    private readonly IDatPathResolver _datPathResolver;
    
    public LaunchCommand(
        ISender sender,
        IFileProvider fileProvider,
        IOperationStatusReporter reporter,
        IDatPathResolver datPathResolver)
    {
        _sender = sender;
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

        [CommandOption("--legacy")]
        [Description("Use legacy launch flow (with DAT protection and process monitoring)")]
        [DefaultValue(false)]
        public bool Legacy { get; init; }
    }
    
    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings,
        CancellationToken cancellationToken)
    {
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
                TranslationFilePath: actualResolvedPaths.Value.TranslationsPath,
                UseLegacyFlow: settings.Legacy);

            Result<GameLaunchingResponse> result = await _sender.Send(gameLaunchingCommand, cancellationToken);
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
