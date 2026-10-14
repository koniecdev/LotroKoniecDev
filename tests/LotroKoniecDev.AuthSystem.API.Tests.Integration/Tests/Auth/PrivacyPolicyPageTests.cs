using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

/// <summary>
/// The privacy policy is legal text, so the owner sets its wording. These lines carry the owner's ruling
/// on #1073, and the claims below were false before it.
/// </summary>
public sealed class PrivacyPolicyPageTests : EndpointsTestBase
{
    private static readonly Uri PrivacyPolicyPage = new("/Account/PrivacyPolicy", UriKind.Relative);

    [Theory]
    [InlineData("hasło zapisane wyłącznie jako jednokierunkowy skrót (hash)")]
    [InlineData("wnieść sprzeciw wobec przetwarzania danych w celu zapewnienia bezpieczeństwa serwisu (art. 21 RODO)")]
    [InlineData("Konto prowadzimy na podstawie umowy, a nie zgody")]
    [InlineData("<strong>Google Ireland Limited (Irlandia)</strong> — skrzynka Gmail")]
    public async Task PrivacyPolicyPage_ShouldStateTheOwnersRuling_WhenAccessed(string statement)
    {
        // Act
        using HttpResponseMessage response = await ApiClient.Http.GetAsync(PrivacyPolicyPage);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldContain(statement);
    }

    [Theory]
    [InlineData("zaszyfrowane hasło")]
    [InlineData("wycofasz zgodę")]
    public async Task PrivacyPolicyPage_ShouldNotMakeAClaimTheSiteDoesNotKeep_WhenAccessed(string claim)
    {
        // Act
        using HttpResponseMessage response = await ApiClient.Http.GetAsync(PrivacyPolicyPage);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldNotContain(claim);
    }

    public PrivacyPolicyPageTests(AuthSystemApiFactory appFactory) : base(appFactory) { }
}
