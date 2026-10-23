using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;

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
        string mailedToken = AccountConfirmationEmailSpy.LastConfirmationTokenSentTo(confirmed.Email)!;

        // Act
        HttpResponseMessage response = await GetConfirmEmailPageAsync(confirmed.Email, mailedToken);
        string html = await response.Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        html.ShouldContain(SuccessHeading);
        html.ShouldNotContain(InvalidLinkMessage);
    }

    /// <summary>
    /// Identity checks the link first and only then refuses the save, so the page must not call the link
    /// dead or offer a new one, and it must not print Identity's English text (#1076). A stored name that
    /// breaks today's name rules makes that refusal happen on every try.
    /// </summary>
    [Fact]
    public async Task ConfirmEmailPage_ShouldSayInPolishThatTheConfirmationFailed_WhenIdentityRefusesAGoodLink()
    {
        // Arrange
        (RegisterRequest unconfirmed, _) =
            await UserFactory.RegisterRandomUserUnconfirmedAsync(ApiClient, Faker, AccountConfirmationEmailSpy);
        string mailedToken = AccountConfirmationEmailSpy.LastConfirmationTokenSentTo(unconfirmed.Email)!;
        await StoreANameTheRulesRefuseAsync(unconfirmed.Email);

        // Act
        HttpResponseMessage response = await GetConfirmEmailPageAsync(unconfirmed.Email, mailedToken);
        string html = await response.Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        html.ShouldContain("Nie udało się potwierdzić adresu");
        html.ShouldContain("Otwórz link z wiadomości jeszcze raz.");
        html.ShouldNotContain("Link wygasł lub jest nieprawidłowy");
        html.ShouldNotContain("Wyślij nowy link aktywacyjny");
        html.ShouldNotContain("is invalid");
    }

    /// <summary>
    /// The store saves without the checks that <c>UserManager</c> runs, so it can keep a name that an older
    /// rule allowed.
    /// </summary>
    private async Task StoreANameTheRulesRefuseAsync(string email)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        UserManager<ApplicationUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        IUserStore<ApplicationUser> store = scope.ServiceProvider.GetRequiredService<IUserStore<ApplicationUser>>();
        ApplicationUser user = (await userManager.FindByEmailAsync(email))!;

        await store.SetUserNameAsync(user, user.UserName + " old", CancellationToken.None);
        await store.UpdateAsync(user, CancellationToken.None);
    }

    private async Task<HttpResponseMessage> GetConfirmEmailPageAsync(string email, string token)
    {
        using HttpClient browser = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        return await browser.GetAsync(new Uri(
            $"/Account/ConfirmEmail?email={Uri.EscapeDataString(email)}&token={Uri.EscapeDataString(token)}",
            UriKind.Relative));
    }
}
