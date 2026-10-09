using Microsoft.Extensions.Options;
using LotroKoniecDev.AuthSystem.API.Services.Maintenance;

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

        // The floor is the token prune's retention, 14 days, which is also the window the privacy policy
        // promises. Every token of the account is then older than the prune keeps a revoked token, so
        // the copy of the name and address in a stored refresh token goes within a day of the erasure,
        // and no access token is still valid to write them back into the TMS profile (ADR-0065).
        if (options.DeletionGracePeriod < OpenIddictPruneService.RetentionPeriod)
        {
            errors.Add(
                $"DeletionGracePeriod must be at least {OpenIddictPruneService.RetentionPeriod.TotalDays:0} days: the token prune keeps a revoked token that long, and the privacy policy promises that window.");
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
