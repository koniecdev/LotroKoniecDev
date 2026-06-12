using LotroKoniecDev.SharedKernel.BuildingBlocks;

namespace LotroKoniecDev.AuthSystem.Infrastructure.Errors;

internal static class InfrastructureErrors
{
    public static class Email
    {
        public static Error SendFailed(string errorMessages) => new(
            $"{nameof(Email)}.{nameof(SendFailed)}",
            $"Failed to send email: {errorMessages}");
    }
}
