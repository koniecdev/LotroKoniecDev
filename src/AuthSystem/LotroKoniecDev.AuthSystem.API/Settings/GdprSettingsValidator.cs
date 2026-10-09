using Microsoft.Extensions.Options;

namespace LotroKoniecDev.AuthSystem.API.Settings;

internal sealed class GdprSettingsValidator : IValidateOptions<GdprSettings>
{
    /// <summary>
    /// GDPR Art. 12(3): the erasure has to happen "without undue delay", and at most one month after
    /// the request.
    /// </summary>
    private static readonly TimeSpan MaxErasureDelay = TimeSpan.FromDays(30);

    /// <summary>
    /// The TMS validates tokens with the JWT handler's default skew, so it accepts a token this long
    /// after it expires.
    /// </summary>
    private static readonly TimeSpan TmsClockSkew = TimeSpan.FromMinutes(5);

    private readonly IOptions<OpenIddictSettings> _openIddictSettings;

    public GdprSettingsValidator(IOptions<OpenIddictSettings> openIddictSettings)
    {
        _openIddictSettings = openIddictSettings;
    }

    public ValidateOptionsResult Validate(string? name, GdprSettings options)
    {
        List<string> errors = [];

        if (options.DeletionGracePeriod <= TimeSpan.Zero)
        {
            errors.Add("DeletionGracePeriod must be positive.");
        }

        // An access token stays valid on the TMS until it expires, plus the TMS's clock skew, and the TMS
        // copies its name and address into the translator profile. Signing in stops when the deletion is
        // scheduled, so a grace period longer than that means no token is left to write them back after
        // the erasure (ADR-0065). QA may still shorten the period to watch a real erasure (ADR-0031).
        TimeSpan accessTokenLifetime = TimeSpan.FromMinutes(_openIddictSettings.Value.AccessTokenLifetimeMinutes);
        TimeSpan tokenAcceptedFor = accessTokenLifetime + TmsClockSkew;
        if (options.DeletionGracePeriod <= tokenAcceptedFor)
        {
            errors.Add(
                $"DeletionGracePeriod must be longer than {tokenAcceptedFor.TotalMinutes:0} minutes: OpenIddict:AccessTokenLifetimeMinutes plus the TMS clock skew, so no access token is still accepted at the erasure.");
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
