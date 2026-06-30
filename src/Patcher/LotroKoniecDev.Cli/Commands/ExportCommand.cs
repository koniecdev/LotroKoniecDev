using System.ComponentModel;
using LotroKoniecDev.Application.Abstractions.DatFilesServices;
using LotroKoniecDev.Application.Abstractions.Messaging;
using LotroKoniecDev.Application.Features.Exporting;
using LotroKoniecDev.Domain.Core.Monads;
using Spectre.Console.Cli;

namespace LotroKoniecDev.Cli.Commands;

internal sealed class ExportCommand : AsyncCommand<ExportCommand.Settings>
{
    private readonly IQueryHandler<ExportTextsQuery, Result<ExportSummaryResponse>> _exportTextsHandler;
    private readonly IDatPathResolver _datPathResolver;
    private readonly IOperationStatusReporter _reporter;

    public ExportCommand(
        IQueryHandler<ExportTextsQuery, Result<ExportSummaryResponse>> exportTextsHandler,
        IDatPathResolver datPathResolver,
        IOperationStatusReporter reporter)
    {
        _exportTextsHandler = exportTextsHandler;
        _datPathResolver = datPathResolver;
        _reporter = reporter;
    }
    
    public sealed class Settings : GlobalSettings
    {
        [CommandOption("-d|--dat-file-path")]
        [Description("Optional path to the DAT file")]
        public string? DatFilePath { get; init; }

        [CommandOption("-o|--output-path-with-filename")]
        [Description("Optional output path. Defaults to data/exported.txt")]
        public string? OutputPath { get; init; }
    }
    
    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        string? actualDatPath = _datPathResolver.Resolve(settings.DatFilePath);
        if (actualDatPath is null)
        {
            return ExitCodes.FileNotFound;
        }
        
        string actualOutputPath = settings.OutputPath ?? Path.Combine(GlobalSettings.DataDir, "exported.txt");

        ExportTextsQuery query = new(
            DatFilePath: actualDatPath,
            OutputPath: actualOutputPath);
        
        try
        {
            Result<ExportSummaryResponse> result = await _exportTextsHandler.Handle(query, cancellationToken);
            if (result.IsFailure)
            {
                _reporter.Report(result.Error.ToString());
                return ErrorMapper.MapErrorToExitCode(result.Error);
            }

            _reporter.Report(result.Value.ToString());
            return ExitCodes.Success;
        }
        catch (OperationCanceledException)
        {
            _reporter.Report("Operation cancelled by user.");
            return ExitCodes.OperationCancelled;
        }
        catch(Exception ex)
        {
            _reporter.Report(ex.ToString());
            return ExitCodes.OperationFailed;
        }
    }
}
