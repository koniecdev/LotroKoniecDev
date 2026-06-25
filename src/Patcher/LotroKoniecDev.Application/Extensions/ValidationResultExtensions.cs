using FluentValidation.Results;
using LotroKoniecDev.Domain.Core.BuildingBlocks;

namespace LotroKoniecDev.Application.Extensions;

public static class ValidationResultExtensions
{
    public static Error ToValidationError(this ValidationResult validationResult, string requestName)
    {
        ArgumentNullException.ThrowIfNull(validationResult);

        string message = string.Join("; ", validationResult.Errors.Select(failure => failure.ErrorMessage));

        return Error.Validation($"{requestName}.Validation", message);
    }
}
