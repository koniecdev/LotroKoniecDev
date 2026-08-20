using Microsoft.Extensions.Options;

namespace LotroKoniecDev.AuthSystem.API.Settings;

internal sealed class GdprSettingsValidator : IValidateOptions<GdprSettings>
{
    public ValidateOptionsResult Validate(string? name, GdprSettings options)
    {
        List<string> errors = [];

        if (options.DeletionGracePeriod <= TimeSpan.Zero)
        {
            errors.Add("DeletionGracePeriod must be positive.");
        }

        // GDPR Art. 12(3): the erasure has to happen "without undue delay", and at most one month
        // after the request.
        if (options.DeletionGracePeriod > TimeSpan.FromDays(30))
        {
            errors.Add("DeletionGracePeriod must not exceed 30 days.");
        }

        if (options.DeletionFinalizationPollInterval < TimeSpan.FromMinutes(1))
        {
            errors.Add("DeletionFinalizationPollInterval must be at least 1 minute.");
        }

        // An interval longer than the grace period would leave the work to the catch-up run at startup
        // alone, and the Art. 12(3) deadline would pass without anyone noticing.
        if (options.DeletionFinalizationPollInterval > options.DeletionGracePeriod)
        {
            errors.Add("DeletionFinalizationPollInterval must not exceed DeletionGracePeriod.");
        }

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
