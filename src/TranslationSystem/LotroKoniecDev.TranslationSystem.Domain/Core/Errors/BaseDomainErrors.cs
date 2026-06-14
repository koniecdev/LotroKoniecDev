using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Enums;

namespace LotroKoniecDev.TranslationSystem.Domain.Core.Errors;

public static partial class DomainErrors
{
    private static Error HasNotBeenFound(string entity, Guid id, string? additionalMessage = null)
    {
        string message = $"The {entity.ToLowerInvariant()} with id: {id} has not been found.";
        if (!string.IsNullOrWhiteSpace(additionalMessage))
        {
            message += $" {additionalMessage}";
        }

        return new Error($"{entity}.NotFound",
            message,
            TypeOfError.NotFound);
    }

    private static Error Required(string entity, string property)
        => new($"{entity}.{property}.NullOrEmpty",
            $"The {property.ToLowerInvariant()} is required.",
            TypeOfError.Validation);

    private static Error AlreadyHasBeenTaken(string entity, string property, object alreadyTakenValue)
        => new($"{entity}.{property}.AlreadyTaken",
            $"The {property.ToLowerInvariant()} value '{alreadyTakenValue}' is already taken.",
            TypeOfError.DataConflict);

    private static Error TooManyCharacters(string entity, string property, int maxLength)
        => new($"{entity}.{property}.LongerThanAllowed",
            $"The {property.ToLowerInvariant()} exceeds maximum length of {maxLength}.",
            TypeOfError.Validation);

    private static Error InvalidOperation(string entity, string message, string code)
        => new($"{entity}.{code}", message, TypeOfError.DataConflict);

    private static Error InvalidDottedNumericFormat(string entity, string property)
        => new($"{entity}.{property}.InvalidFormat",
            $"The {property.ToLowerInvariant()} must be dotted-numeric notation (e.g. '48.0', '47.1.1').",
            TypeOfError.Validation);
}
