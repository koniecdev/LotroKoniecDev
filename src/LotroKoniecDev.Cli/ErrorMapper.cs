using LotroKoniecDev.Domain.Core.BuildingBlocks;
using LotroKoniecDev.Primitives.Enums;

namespace LotroKoniecDev.Cli;

public static class ErrorMapper
{
    public static int MapErrorToExitCode(Error error) =>
        error.Type switch
        {
            ErrorType.NotFound => ExitCodes.FileNotFound,
            ErrorType.Validation => ExitCodes.InvalidArguments,
            _ => ExitCodes.OperationFailed
        };
}
