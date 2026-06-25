using LotroKoniecDev.Domain.Core.BuildingBlocks;
using LotroKoniecDev.Primitives.Enums;

namespace LotroKoniecDev.Domain.Core.Errors;

public static partial class DomainErrors
{
    private static Error NotFound(string resource, string identifier) =>
        new($"{resource}.NotFound",
            $"{resource} not found: {identifier}",
            ErrorType.NotFound);

    private static Error IoError(string resource, string operation, string details) =>
        new($"{resource}.{operation}",
            $"{operation} failed for {resource.ToLowerInvariant()}: {details}",
            ErrorType.IoError);

    private static Error InvalidFormat(string resource, string details) =>
        new($"{resource}.InvalidFormat",
            $"Invalid {resource.ToLowerInvariant()} format: {details}",
            ErrorType.Validation);

    private static Error OperationFailed(string resource, string message) =>
        new($"{resource}.Failed",
            message,
            ErrorType.Failure);
}
