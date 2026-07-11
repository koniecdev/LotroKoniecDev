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

        // GDPR Art. 12(3): erasure must complete "without undue delay",
        // at most one month after the request.
        if (options.DeletionGracePeriod > TimeSpan.FromDays(30))
        {
            errors.Add("DeletionGracePeriod must not exceed 30 days.");
        }

        if (options.DeletionFinalizationPollInterval < TimeSpan.FromMinutes(1))
        {
            errors.Add("DeletionFinalizationPollInterval must be at least 1 minute.");
        }

        // A poll interval longer than the grace period would leave finalization to the
        // startup catch-up run alone and silently overshoot the Art. 12(3) deadline.
        if (options.DeletionFinalizationPollInterval > options.DeletionGracePeriod)
        {
            errors.Add("DeletionFinalizationPollInterval must not exceed DeletionGracePeriod.");
        }

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
