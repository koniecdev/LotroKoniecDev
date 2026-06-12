using FluentValidation.Results;
using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Enums;

namespace LotroKoniecDev.AuthSystem.API.Extensions;

internal static class ValidationResultExtensions
{
    public static Error ToValidationError(this ValidationResult validationResult, string requestName)
    {
        ArgumentNullException.ThrowIfNull(validationResult);

        string message = string.Join("; ", validationResult.Errors.Select(failure => failure.ErrorMessage));

        return new Error($"{requestName}.Validation", message, TypeOfError.Validation);
    }
}
