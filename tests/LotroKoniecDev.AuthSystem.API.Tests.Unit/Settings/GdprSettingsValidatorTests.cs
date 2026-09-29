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
    private readonly GdprSettingsValidator _validator = new();

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
    [InlineData("14.00:00:00")]
    public void Validate_PollIntervalFromOneMinuteUpToTheGracePeriod_Succeeds(string pollInterval)
    {
        GdprSettings settings = new()
        {
            DeletionGracePeriod = TimeSpan.FromDays(14),
            DeletionFinalizationPollInterval = Parse(pollInterval)
        };

        ValidateOptionsResult result = _validator.Validate(name: null, settings);

        result.Succeeded.ShouldBeTrue();
    }

    [Theory]
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
    [InlineData("14.00:00:00.0000001")]
    [InlineData("15.00:00:00")]
    // This is how one day is easy to get wrong in appsettings.json: it binds to 24 days (#780).
    [InlineData("24:00:00")]
    public void Validate_PollIntervalLongerThanTheGracePeriod_FailsNamingTheSetting(string pollInterval)
    {
        GdprSettings settings = new()
        {
            DeletionGracePeriod = TimeSpan.FromDays(14),
            DeletionFinalizationPollInterval = Parse(pollInterval)
        };

        ValidateOptionsResult result = _validator.Validate(name: null, settings);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldNotBeNull();
        result.Failures.ShouldContain(failure =>
            failure.Contains("DeletionFinalizationPollInterval must not exceed DeletionGracePeriod", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("1.00:00:00")]
    [InlineData("30.00:00:00")]
    public void Validate_GracePeriodFromOneDayUpToThirtyDaysWithADailyPoll_Succeeds(string gracePeriod)
    {
        GdprSettings settings = new()
        {
            DeletionGracePeriod = Parse(gracePeriod),
            DeletionFinalizationPollInterval = TimeSpan.FromDays(1)
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
    [InlineData("30.00:00:00.0000001")]
    [InlineData("31.00:00:00")]
    public void Validate_GracePeriodLongerThanThirtyDays_FailsNamingTheSetting(string gracePeriod)
    {
        GdprSettings settings = new() { DeletionGracePeriod = Parse(gracePeriod) };

        ValidateOptionsResult result = _validator.Validate(name: null, settings);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldNotBeNull();
        result.Failures.ShouldContain(failure =>
            failure.Contains("DeletionGracePeriod must not exceed 30 days", StringComparison.Ordinal));
    }

    private static TimeSpan Parse(string value) => TimeSpan.Parse(value, CultureInfo.InvariantCulture);
}
