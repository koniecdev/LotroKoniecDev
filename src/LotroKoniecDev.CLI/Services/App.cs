using LotroKoniecDev.CLI.Application;
using LotroKoniecDev.CLI.Domain.ProgramArgs;
using LotroKoniecDev.CLI.Errors;
using LotroKoniecDev.CLI.Guards;
using LotroKoniecDev.Domain.Core.BuildingBlocks;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Primitives.Enums;

namespace LotroKoniecDev.CLI.Services;

internal sealed class App
{
    private readonly IExportService _exportService;
    private readonly IPatchService _patchService;

    public App(IExportService exportService, IPatchService patchService)
    {
        _exportService = exportService;
        _patchService = patchService;
    }
    
    internal async Task<Result> RunAsync(string[] args)
    {
        if (!ArgsValidator.IsValid(args))
        {
            return Result.Failure(
                new Error("InvalidArgumentsCount", "Invalid arguments count", ErrorType.Validation));
        }

        Result<CommandArg> commandArgResult = CommandArg.Create(args[0]);
        if (commandArgResult.IsFailure)
        {
            return Result.Failure(commandArgResult.Error);
        }
        
        CommandArg commandArg = commandArgResult.Value;

        switch (commandArg.Value)
        {
            case "export":
                return await _exportService.ExportAsync();
            case "patch":
                return await _patchService.PatchAsync();
            default:
                return Result.Failure(
                    new Error("InvalidCommand", $"Invalid command: {commandArg.Value}", ErrorType.Validation));
        }
        
        return ExitCodes.Success;
    }
}
