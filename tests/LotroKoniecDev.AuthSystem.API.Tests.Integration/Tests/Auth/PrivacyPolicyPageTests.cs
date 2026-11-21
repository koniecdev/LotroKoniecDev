using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

/// <summary>
/// The privacy policy is legal text, so the owner sets its wording. These lines pin the wording his rulings
/// and reviews settled, so a later edit cannot drop one unnoticed.
/// </summary>
public sealed class PrivacyPolicyPageTests : EndpointsTestBase
{
    private static readonly Uri PrivacyPolicyPage = new("/Account/PrivacyPolicy", UriKind.Relative);

    [Theory]
    [InlineData("hasło zapisane wyłącznie jako jednokierunkowy skrót (hash)")]
    [InlineData("wnieść sprzeciw wobec przetwarzania danych w celu zapewnienia bezpieczeństwa serwisu (art. 21 RODO)")]
    [InlineData("Konto prowadzimy na podstawie umowy, a nie zgody")]
    [InlineData("<strong>Google Ireland Limited (Irlandia)</strong> — skrzynka Gmail")]
    [InlineData("<strong>Cloudflare, Inc.</strong> — przechowywanie zaszyfrowanych kopii zapasowych bazy danych w usłudze R2")]
    [InlineData("przechowujemy poza dostawcą bazy przez <strong>30 dni</strong> od jej utworzenia")]
    [InlineData("dane kont usuniętych po jej utworzeniu usuwamy ponownie, zanim wrócą do serwisu")]
    [InlineData("wyłącznie do odtworzenia danych po awarii lub nadużyciu oraz do okresowego sprawdzenia, że da się je odtworzyć")]
    [InlineData("<strong>Cookie informacji o plikach cookie</strong> — zapamiętuje, że w tej przeglądarce wyświetliliśmy już informację o plikach cookie")]
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
    [InlineData("obejmuje okres do <strong>6 godzin</strong> wstecz")]
    [InlineData("Cookie zgody na pliki cookie")]
    [InlineData("zapoznałeś się z informacją o plikach cookie")]
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
