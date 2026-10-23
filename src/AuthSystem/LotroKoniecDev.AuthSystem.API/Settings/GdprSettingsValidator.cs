using Microsoft.Extensions.Options;

namespace LotroKoniecDev.AuthSystem.API.Settings;

internal sealed class GdprSettingsValidator : IValidateOptions<GdprSettings>
{
    /// <summary>
    /// GDPR Art. 12(3) allows at most one month, and the shortest month has 28 days, so 27 leaves a day
    /// to spare. The poll interval counts inside the 27 days (ADR-0031, #971).
    /// </summary>
    private static readonly TimeSpan MaxErasureDelay = TimeSpan.FromDays(27);

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

        // The TMS copies a token's name and address into the translator profile and accepts the token
        // until it expires. A grace period longer than the token lifetime means no token is left to write
        // them back after the erasure (ADR-0065).
        TimeSpan accessTokenLifetime = TimeSpan.FromMinutes(_openIddictSettings.Value.AccessTokenLifetimeMinutes);
        if (options.DeletionGracePeriod <= accessTokenLifetime)
        {
            errors.Add(
                $"DeletionGracePeriod must be longer than {accessTokenLifetime.TotalMinutes:0} minutes: OpenIddict:AccessTokenLifetimeMinutes, so no access token is still accepted at the erasure.");
        }

        if (options.DeletionFinalizationPollInterval < TimeSpan.FromMinutes(1))
        {
            errors.Add("DeletionFinalizationPollInterval must be at least 1 minute.");
        }

        // The erasure lands up to one interval after the date the user is shown. This rule also keeps the
        // e-mail change undo hold inside one month (ADR-0031, #946 amendment).
        if (options.DeletionFinalizationPollInterval > options.DeletionGracePeriod)
        {
            errors.Add("DeletionFinalizationPollInterval must not exceed DeletionGracePeriod.");
        }

        // Checked by subtraction, with a non-positive interval as zero, so a huge value ends in a
        // validation message, not an OverflowException.
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
