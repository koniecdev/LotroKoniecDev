using Microsoft.Extensions.Options;

namespace LotroKoniecDev.AuthSystem.API.Settings;

internal sealed class GdprSettingsValidator : IValidateOptions<GdprSettings>
{
    /// <summary>
    /// GDPR Art. 12(3): the erasure has to happen "without undue delay", and at most one month after
    /// the request.
    /// </summary>
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

        // The erasure lands up to one interval after the date the user is shown, so a longer interval
        // than the grace period would make that wait longer than the window itself. This rule also keeps
        // the email-change undo hold inside the limit below (ADR-0031, #946 amendment).
        if (options.DeletionFinalizationPollInterval > options.DeletionGracePeriod)
        {
            errors.Add("DeletionFinalizationPollInterval must not exceed DeletionGracePeriod.");
        }

        // The interval comes on top of the grace period (#946). The sum is checked by subtraction,
        // and a non-positive interval counts as zero, so a huge value in appsettings.json ends in a
        // validation message and not in an OverflowException.
        TimeSpan pollDelay = options.DeletionFinalizationPollInterval > TimeSpan.Zero
            ? options.DeletionFinalizationPollInterval
            : TimeSpan.Zero;

        if (options.DeletionGracePeriod > MaxErasureDelay - pollDelay)
        {
            errors.Add(
                $"DeletionGracePeriod plus DeletionFinalizationPollInterval must not exceed {MaxErasureDelay.TotalDays:0} days.");
        }

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
