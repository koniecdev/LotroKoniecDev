using Microsoft.AspNetCore.Mvc.Testing;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

/// <summary>
/// The success page names the account, so for an address that is already confirmed it needs the mailed
/// token, like every other branch. Without it the page told anyone which addresses have a confirmed
/// account (ADR-0059 §7).
/// </summary>
public sealed class ConfirmEmailPageTests : EndpointsTestBase
{
    private const string SuccessHeading = "Twój adres e-mail został potwierdzony";
    private const string InvalidLinkMessage = "Link potwierdzający jest nieprawidłowy lub wygasł.";

    public ConfirmEmailPageTests(AuthSystemApiFactory appFactory) : base(appFactory) { }

    [Fact]
    public async Task ConfirmEmailPage_ShouldSayTheLinkIsInvalid_WhenTheAccountIsConfirmedAndTheTokenIsWrong()
    {
        // Arrange
        (RegisterRequest confirmed, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy);

        // Act
        HttpResponseMessage response = await GetConfirmEmailPageAsync(confirmed.Email, "not-the-mailed-token");
        string html = await response.Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        html.ShouldContain(InvalidLinkMessage);
        html.ShouldNotContain(SuccessHeading);
    }

    /// <summary>
    /// The owner clicks the mailed link a second time. Confirming does not change the security stamp, so
    /// the token still verifies and the page still says the account is active.
    /// </summary>
    [Fact]
    public async Task ConfirmEmailPage_ShouldShowSuccess_WhenTheOwnerOpensTheMailedLinkASecondTime()
    {
        // Arrange: registration confirms the account with the mailed token
        (RegisterRequest confirmed, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy);
        string mailedToken = AccountConfirmationEmailSpy.LastConfirmationToken!;

        // Act
        HttpResponseMessage response = await GetConfirmEmailPageAsync(confirmed.Email, mailedToken);
        string html = await response.Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        html.ShouldContain(SuccessHeading);
        html.ShouldNotContain(InvalidLinkMessage);
    }

    private async Task<HttpResponseMessage> GetConfirmEmailPageAsync(string email, string token)
    {
        using HttpClient browser = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        return await browser.GetAsync(new Uri(
            $"/Account/ConfirmEmail?email={Uri.EscapeDataString(email)}&token={Uri.EscapeDataString(token)}",
            UriKind.Relative));
    }
}
