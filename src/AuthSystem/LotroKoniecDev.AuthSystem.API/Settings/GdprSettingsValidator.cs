using Microsoft.Extensions.Options;

namespace LotroKoniecDev.AuthSystem.API.Settings;

internal sealed class GdprSettingsValidator : IValidateOptions<GdprSettings>
{
    // GDPR Art. 12(3): the erasure has to happen "without undue delay", and at most one month
    // after the request.
    private static readonly TimeSpan MaxErasureDelay = TimeSpan.FromDays(30);

    public ValidateOptionsResult Validate(string? name, GdprSettings options)
    {
        List<string> errors = [];

        if (options.DeletionGracePeriod <= TimeSpan.Zero)
        {
            errors.Add("DeletionGracePeriod must be positive.");
        }

        if (options.DeletionFinalizationPollInterval < TimeSpan.FromMinutes(1))
        {
            errors.Add("DeletionFinalizationPollInterval must be at least 1 minute.");
        }

        // The erasure lands at the first run after the grace period, so up to one interval after the
        // date the user is shown. A longer interval than the grace period would make that wait longer
        // than the window itself.
        if (options.DeletionFinalizationPollInterval > options.DeletionGracePeriod)
        {
            errors.Add("DeletionFinalizationPollInterval must not exceed DeletionGracePeriod.");
        }

        // The grace period alone is not the deadline: the interval comes on top of it (#946). The sum
        // is checked by subtraction, so a huge value in appsettings.json ends in this message and not
        // in an OverflowException. A non-positive interval already failed above.
        if (options.DeletionFinalizationPollInterval > TimeSpan.Zero
            && options.DeletionGracePeriod > MaxErasureDelay - options.DeletionFinalizationPollInterval)
        {
            errors.Add("DeletionGracePeriod plus DeletionFinalizationPollInterval must not exceed 30 days.");
        }

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
