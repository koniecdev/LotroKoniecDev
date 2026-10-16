using System.Text.Json.Nodes;

namespace LotroKoniecDev.Frontend.Tests.Integration.Tests.Settings;

/// <summary>
/// Keeps the csproj's copy of the Frontend's navigation switch equal to the Frontend's own value (#1122),
/// so the suite never quietly stops behaving like a deployed box.
/// </summary>
public sealed class TestHostRuntimeConfigTests
{
    private const string NavigationSwitch =
        "Microsoft.AspNetCore.Components.Endpoints.NavigationManager.DisableThrowNavigationException";

    [Fact]
    public void NavigationSwitch_InThisHost_MatchesTheFrontendRuntimeConfig()
    {
        // Arrange
        JsonNode? frontendSwitch = JsonNode.Parse(File.ReadAllText(
                Path.Combine(AppContext.BaseDirectory, "LotroKoniecDev.Frontend.runtimeconfig.json")))
            ?["runtimeOptions"]?["configProperties"]?[NavigationSwitch];

        // Act
        bool isSetHere = AppContext.TryGetSwitch(NavigationSwitch, out bool valueHere);

        // Assert
        (isSetHere ? valueHere : (bool?)null).ShouldBe(frontendSwitch?.GetValue<bool>());
    }
}
