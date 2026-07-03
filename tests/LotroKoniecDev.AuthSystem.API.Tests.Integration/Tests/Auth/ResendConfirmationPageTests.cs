using System.Text.RegularExpressions;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.SharedKernel.Constants;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

public sealed partial class ResendConfirmationPageTests : EndpointsTestBase
{
    private const string SuccessMarker = "data-testid=\"resend-confirmation-success\"";

    public ResendConfirmationPageTests(AuthSystemApiFactory appFactory) : base(appFactory) { }

    [Fact]
    public async Task ResendConfirmationPage_ShouldReturnOk_WhenAccessed()
    {
        // Act
        HttpResponseMessage response = await ApiClient.Http.GetAsync(
            new Uri("/Account/ResendConfirmation", UriKind.Relative));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        string html = await response.Content.ReadAsStringAsync();
        html.ShouldContain("Link aktywacyjny");
        html.ShouldContain("Adres e-mail");
        html.ShouldContain("Wyślij link aktywacyjny");
    }

    [Fact]
    public async Task LoginPage_ShouldOfferLinkToResendConfirmation()
    {
        // Act
        HttpResponseMessage response = await ApiClient.Http.GetAsync(
            new Uri("/Account/Login", UriKind.Relative));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        string html = await response.Content.ReadAsStringAsync();
        html.ShouldContain("/Account/ResendConfirmation");
    }

    [Fact]
    public async Task ConfirmEmailPage_ShouldOfferLinkToResendConfirmation_WhenTokenIsInvalid()
    {
        // Act — a bare ConfirmEmail GET (no email/token) renders the expired/invalid panel
        HttpResponseMessage response = await ApiClient.Http.GetAsync(
            new Uri("/Account/ConfirmEmail", UriKind.Relative));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        string html = await response.Content.ReadAsStringAsync();
        html.ShouldContain("Link wygasł lub jest nieprawidłowy");
        html.ShouldContain("/Account/ResendConfirmation");
    }

    [Fact]
    public async Task ResendConfirmationPage_ShouldSendConfirmationEmail_WhenAccountIsUnconfirmed()
    {
        // Arrange
        (RegisterRequest request, _) =
            await UserFactory.RegisterRandomUserUnconfirmedAsync(ApiClient, Faker, AccountConfirmationEmailSpy);

        AccountConfirmationEmailSpy.Reset();

        // Act
        HttpResponseMessage response = await PostToResendConfirmationPageAsync(request.Email);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        string html = await response.Content.ReadAsStringAsync();
        html.ShouldContain(SuccessMarker);

        AccountConfirmationEmailSpy.CallCount.ShouldBe(1);
        AccountConfirmationEmailSpy.LastEmail.ShouldBe(request.Email);
        AccountConfirmationEmailSpy.LastConfirmationToken.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task ResendConfirmationPage_ShouldNotSendEmail_WhenAccountIsAlreadyConfirmed()
    {
        // Arrange
        (RegisterRequest request, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy);

        AccountConfirmationEmailSpy.Reset();

        // Act
        HttpResponseMessage response = await PostToResendConfirmationPageAsync(request.Email);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        string html = await response.Content.ReadAsStringAsync();
        html.ShouldContain(SuccessMarker);

        AccountConfirmationEmailSpy.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task ResendConfirmationPage_ShouldNotSendEmail_WhenEmailIsUnknown()
    {
        // Arrange
        AccountConfirmationEmailSpy.Reset();

        // Act
        HttpResponseMessage response = await PostToResendConfirmationPageAsync("nobody@example.com");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        string html = await response.Content.ReadAsStringAsync();
        html.ShouldContain(SuccessMarker);

        AccountConfirmationEmailSpy.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task ResendConfirmationPage_ShouldRenderIdenticalConfirmation_ForUnknownConfirmedAndUnconfirmed()
    {
        // Arrange — one account per confirmation state, plus a never-registered address
        (RegisterRequest unconfirmed, _) =
            await UserFactory.RegisterRandomUserUnconfirmedAsync(ApiClient, Faker, AccountConfirmationEmailSpy);
        (RegisterRequest confirmed, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy);
        string unknownEmail = Faker.Internet.Email();

        // Act — the neutral confirmation each input produces
        string unconfirmedHtml = await (await PostToResendConfirmationPageAsync(unconfirmed.Email)).Content.ReadAsStringAsync();
        string confirmedHtml = await (await PostToResendConfirmationPageAsync(confirmed.Email)).Content.ReadAsStringAsync();
        string unknownHtml = await (await PostToResendConfirmationPageAsync(unknownEmail)).Content.ReadAsStringAsync();

        // Assert — anti-enumeration: the rendered confirmation must be byte-identical
        unconfirmedHtml.ShouldContain(SuccessMarker);
        confirmedHtml.ShouldBe(unconfirmedHtml);
        unknownHtml.ShouldBe(unconfirmedHtml);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-email")]
    [InlineData("missing-domain@")]
    [InlineData("with spaces@example.com")]
    public async Task ResendConfirmationPage_ShouldShowOpaqueError_AndNotSend_WhenEmailIsInvalid(string invalidEmail)
    {
        // Arrange
        AccountConfirmationEmailSpy.Reset();

        // Act
        HttpResponseMessage response = await PostToResendConfirmationPageAsync(invalidEmail);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        string html = await response.Content.ReadAsStringAsync();
        html.ShouldContain("Podaj prawidłowy adres e-mail.");
        html.ShouldNotContain(SuccessMarker);

        AccountConfirmationEmailSpy.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task ResendConfirmationPage_ShouldShowOpaqueError_AndNotSend_WhenEmailExceedsMaxLength()
    {
        // Arrange — one over EmailConstants.MaxLength, so the shared validator rejects it before any lookup
        AccountConfirmationEmailSpy.Reset();
        string overLongEmail = new string('a', EmailConstants.MaxLength) + "@example.com";

        // Act
        HttpResponseMessage response = await PostToResendConfirmationPageAsync(overLongEmail);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        string html = await response.Content.ReadAsStringAsync();
        html.ShouldContain("Podaj prawidłowy adres e-mail.");
        html.ShouldNotContain(SuccessMarker);

        AccountConfirmationEmailSpy.CallCount.ShouldBe(0);
    }

    private async Task<HttpResponseMessage> PostToResendConfirmationPageAsync(string email)
    {
        HttpResponseMessage pageResponse = await ApiClient.Http.GetAsync(
            new Uri("/Account/ResendConfirmation", UriKind.Relative));
        pageResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        string html = await pageResponse.Content.ReadAsStringAsync();
        Dictionary<string, string> formFields = new() { ["Email"] = email };

        string? antiForgeryToken = ExtractAntiForgeryToken(html);
        if (antiForgeryToken is not null)
        {
            formFields["__RequestVerificationToken"] = antiForgeryToken;
        }

        using FormUrlEncodedContent content = new(formFields);
        using HttpRequestMessage request = new(HttpMethod.Post, "/Account/ResendConfirmation");
        request.Content = content;

        if (pageResponse.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? cookies))
        {
            foreach (string cookie in cookies)
            {
                request.Headers.Add("Cookie", cookie.Split(';')[0]);
            }
        }

        return await ApiClient.Http.SendAsync(request);
    }

    private static string? ExtractAntiForgeryToken(string html)
    {
        Match match = AntiForgeryTokenRegex().Match(html);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex("""name="__RequestVerificationToken".*?value="([^"]+)""")]
    private static partial Regex AntiForgeryTokenRegex();
}
