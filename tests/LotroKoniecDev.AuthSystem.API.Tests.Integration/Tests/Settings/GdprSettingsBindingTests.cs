using LotroKoniecDev.AuthSystem.API.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Settings;

/// <summary>
/// Reads the finalizer's poll interval the way the running app gets it: from the shipped
/// appsettings.json, through configuration binding. The factory does not pin it, so a value that
/// quietly binds to something else, or a change back to hourly polling, fails here and not on the
/// Neon bill (#780).
/// </summary>
[Collection("AuthApi")]
public sealed class GdprSettingsBindingTests
{
    private readonly AuthSystemApiFactory _factory;

    public GdprSettingsBindingTests(AuthSystemApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public void ShippedConfiguration_DeletionFinalizationPollInterval_IsOneDay()
    {
        GdprSettings settings = _factory.Services.GetRequiredService<IOptions<GdprSettings>>().Value;

        settings.DeletionFinalizationPollInterval.ShouldBe(TimeSpan.FromDays(1));
    }

    [Fact]
    public void ShippedConfiguration_DeletionFinalizationPollInterval_MatchesTheCodeDefault()
    {
        GdprSettings settings = _factory.Services.GetRequiredService<IOptions<GdprSettings>>().Value;

        settings.DeletionFinalizationPollInterval.ShouldBe(new GdprSettings().DeletionFinalizationPollInterval);
    }
}
