using System.Globalization;
using LotroKoniecDev.AuthSystem.API.Settings;
using Microsoft.Extensions.Options;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Settings;

/// <summary>
/// The startup guard for the GDPR deletion timings (ADR-0031). The values are written the way
/// appsettings.json writes them, because that text is what the app reads at startup.
/// </summary>
public sealed class GdprSettingsValidatorTests
{
    private readonly GdprSettingsValidator _validator = new(
        Microsoft.Extensions.Options.Options.Create(new OpenIddictSettings { Issuer = "https://auth.lotro-translator.test" }));

    [Fact]
    public void Validate_DefaultSettings_Succeeds()
    {
        GdprSettings settings = new();

        ValidateOptionsResult result = _validator.Validate(name: null, settings);

        result.Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData("00:01:00")]
    [InlineData("01:00:00")]
    [InlineData("1.00:00:00")]
    [InlineData("13.00:00:00")]
    public void Validate_PollIntervalFromOneMinuteUpToTheGracePeriod_Succeeds(string pollInterval)
    {
        GdprSettings settings = new()
        {
            DeletionGracePeriod = TimeSpan.FromDays(13),
            DeletionFinalizationPollInterval = Parse(pollInterval)
        };

        ValidateOptionsResult result = _validator.Validate(name: null, settings);

        result.Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData("-10675199.02:48:05.4775808")]
    [InlineData("-00:01:00")]
    [InlineData("00:00:00")]
    [InlineData("00:00:59")]
    public void Validate_PollIntervalShorterThanOneMinute_FailsNamingTheSetting(string pollInterval)
    {
        GdprSettings settings = new() { DeletionFinalizationPollInterval = Parse(pollInterval) };

        ValidateOptionsResult result = _validator.Validate(name: null, settings);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldNotBeNull();
        result.Failures.ShouldContain(failure =>
            failure.Contains("DeletionFinalizationPollInterval must be at least 1 minute", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("13.00:00:00.0000001")]
    [InlineData("14.00:00:00")]
    // This is how one day is easy to get wrong in appsettings.json: it binds to 24 days (#780).
    [InlineData("24:00:00")]
    public void Validate_PollIntervalLongerThanTheGracePeriod_FailsNamingTheSetting(string pollInterval)
    {
        GdprSettings settings = new()
        {
            DeletionGracePeriod = TimeSpan.FromDays(13),
            DeletionFinalizationPollInterval = Parse(pollInterval)
        };

        ValidateOptionsResult result = _validator.Validate(name: null, settings);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldNotBeNull();
        result.Failures.ShouldContain(failure =>
            failure.Contains("DeletionFinalizationPollInterval must not exceed DeletionGracePeriod", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("1.00:00:00", "1.00:00:00")]
    [InlineData("14.00:00:00", "1.00:00:00")]
    [InlineData("26.00:00:00", "1.00:00:00")]
    [InlineData("13.12:00:00", "13.12:00:00")]
    [InlineData("26.23:59:00", "00:01:00")]
    public void Validate_GracePeriodPlusPollIntervalUpToTwentySevenDays_Succeeds(string gracePeriod, string pollInterval)
    {
        GdprSettings settings = new()
        {
            DeletionGracePeriod = Parse(gracePeriod),
            DeletionFinalizationPollInterval = Parse(pollInterval)
        };

        ValidateOptionsResult result = _validator.Validate(name: null, settings);

        result.Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData("00:01:00")]
    [InlineData("00:04:59")]
    [InlineData("00:05:00")]
    public void Validate_GracePeriodNoLongerThanAnAccessTokenIsAccepted_FailsNamingTheSetting(string gracePeriod)
    {
        // The default access token lives five minutes, and the TMS accepts it not a second longer (#933).
        GdprSettings settings = new()
        {
            DeletionGracePeriod = Parse(gracePeriod),
            DeletionFinalizationPollInterval = TimeSpan.FromMinutes(1)
        };

        ValidateOptionsResult result = _validator.Validate(name: null, settings);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldNotBeNull();
        result.Failures.ShouldContain(failure =>
            failure.Contains("DeletionGracePeriod must be longer than 5 minutes", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("00:05:01")]
    [InlineData("00:10:00")]
    [InlineData("00:30:00")]
    public void Validate_ShortGracePeriodForQa_SucceedsOnceItOutlivesAnAccessToken(string gracePeriod)
    {
        // ADR-0031: QA shortens the grace period to watch a real erasure. With no clock skew on the TMS
        // (#933), a grace period just past the token lifetime is enough.
        GdprSettings settings = new()
        {
            DeletionGracePeriod = Parse(gracePeriod),
            DeletionFinalizationPollInterval = TimeSpan.FromMinutes(1)
        };

        ValidateOptionsResult result = _validator.Validate(name: null, settings);

        result.Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData("-1.00:00:00")]
    [InlineData("00:00:00")]
    public void Validate_GracePeriodNotPositive_FailsNamingTheSetting(string gracePeriod)
    {
        GdprSettings settings = new() { DeletionGracePeriod = Parse(gracePeriod) };

        ValidateOptionsResult result = _validator.Validate(name: null, settings);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldNotBeNull();
        result.Failures.ShouldContain(failure =>
            failure.Contains("DeletionGracePeriod must be positive", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("26.00:00:00.0000001", "1.00:00:00")]
    [InlineData("27.00:00:00", "1.00:00:00")]
    [InlineData("27.00:00:00", "00:01:00")]
    // The old 30-day limit let this through, so a request made on 1 February could be erased on 3 March (#971).
    [InlineData("29.00:00:00", "1.00:00:00")]
    // The erasure could land on day 31 with these two (#946).
    [InlineData("30.00:00:00", "1.00:00:00")]
    [InlineData("30.00:00:00", "00:01:00")]
    [InlineData("13.12:00:00.0000001", "13.12:00:00")]
    [InlineData("27.00:00:00.0000001", "00:00:00")]
    [InlineData("27.00:00:00.0000001", "-10675199.02:48:05.4775808")]
    [InlineData("10675199.02:48:05.4775807", "1.00:00:00")]
    [InlineData("14.00:00:00", "10675199.02:48:05.4775807")]
    public void Validate_GracePeriodPlusPollIntervalLongerThanTwentySevenDays_FailsNamingBothSettings(
        string gracePeriod,
        string pollInterval)
    {
        GdprSettings settings = new()
        {
            DeletionGracePeriod = Parse(gracePeriod),
            DeletionFinalizationPollInterval = Parse(pollInterval)
        };

        ValidateOptionsResult result = _validator.Validate(name: null, settings);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldNotBeNull();
        result.Failures.ShouldContain(failure =>
            failure.Contains(
                "DeletionGracePeriod plus DeletionFinalizationPollInterval must not exceed 27 days",
                StringComparison.Ordinal));
    }

    private static TimeSpan Parse(string value) => TimeSpan.Parse(value, CultureInfo.InvariantCulture);
}
