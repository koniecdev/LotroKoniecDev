using System.Text.RegularExpressions;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

public sealed partial class RegisterPageTests : EndpointsTestBase
{
    public RegisterPageTests(AuthSystemApiFactory appFactory) : base(appFactory) { }

    [Fact]
    public async Task RegisterPage_ShouldReturnOk_WhenAccessed()
    {
        // Act
        HttpResponseMessage response = await ApiClient.Http.GetAsync(
            new Uri("/Account/Register", UriKind.Relative));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        string html = await response.Content.ReadAsStringAsync();
        html.ShouldContain("Załóż konto");
        html.ShouldContain("Nazwa użytkownika");
        html.ShouldContain("Adres e-mail");
    }

    [Fact]
    public async Task RegisterPage_ShouldCreateUserAndShowSuccess_WhenDataIsValid()
    {
        // Arrange
        AccountConfirmationEmailSpy.Reset();
        RegisterRequest request = UserFactory.GenerateRandomRegisterRequest(Faker);

        // Act
        HttpResponseMessage response = await PostToRegisterPageAsync(BuildForm(request, request.Password));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        string html = await response.Content.ReadAsStringAsync();
        html.ShouldContain("Konto zostało utworzone");
        AccountConfirmationEmailSpy.LastEmail.ShouldBe(request.Email);
    }

    [Fact]
    public async Task RegisterPage_ShouldShowError_WhenPasswordsDoNotMatch()
    {
        // Arrange
        AccountConfirmationEmailSpy.Reset();
        RegisterRequest request = UserFactory.GenerateRandomRegisterRequest(Faker);

        // Act
        HttpResponseMessage response = await PostToRegisterPageAsync(
            BuildForm(request, request.Password + "X"));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        string html = await response.Content.ReadAsStringAsync();
        html.ShouldContain("Hasła nie są identyczne");
        AccountConfirmationEmailSpy.LastEmail.ShouldBeNull();
    }

    [Fact]
    public async Task RegisterPage_ShouldShowError_WhenPrivacyPolicyNotAccepted()
    {
        // Arrange
        AccountConfirmationEmailSpy.Reset();
        RegisterRequest request = UserFactory.GenerateRandomRegisterRequest(Faker);

        // Act
        HttpResponseMessage response = await PostToRegisterPageAsync(
            BuildForm(request, request.Password, acceptedPrivacyPolicy: false));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        string html = await response.Content.ReadAsStringAsync();
        html.ShouldContain("Musisz zaakceptować politykę prywatności");
        AccountConfirmationEmailSpy.LastEmail.ShouldBeNull();
    }

    [Fact]
    public async Task RegisterPage_ShouldShowError_WhenEmailAlreadyExists()
    {
        // Arrange
        (RegisterRequest existing, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy);

        RegisterRequest request = UserFactory.GenerateRandomRegisterRequest(Faker);
        Dictionary<string, string> form = BuildForm(request, request.Password);
        form["Email"] = existing.Email;

        // Act
        HttpResponseMessage response = await PostToRegisterPageAsync(form);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        string html = await response.Content.ReadAsStringAsync();
        html.ShouldContain("Konto z tym adresem e-mail już istnieje");
    }

    private static Dictionary<string, string> BuildForm(
        RegisterRequest request,
        string confirmPassword,
        bool acceptedPrivacyPolicy = true,
        bool acceptedDataProcessingConsent = true) => new()
        {
            ["Username"] = request.Username,
            ["Email"] = request.Email,
            ["Password"] = request.Password,
            ["ConfirmPassword"] = confirmPassword,
            ["AcceptedPrivacyPolicy"] = acceptedPrivacyPolicy ? "true" : "false",
            ["AcceptedDataProcessingConsent"] = acceptedDataProcessingConsent ? "true" : "false"
        };

    private async Task<HttpResponseMessage> PostToRegisterPageAsync(Dictionary<string, string> formFields)
    {
        HttpResponseMessage pageResponse = await ApiClient.Http.GetAsync(
            new Uri("/Account/Register", UriKind.Relative));
        pageResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        string html = await pageResponse.Content.ReadAsStringAsync();
        string? antiForgeryToken = ExtractAntiForgeryToken(html);
        if (antiForgeryToken is not null)
        {
            formFields["__RequestVerificationToken"] = antiForgeryToken;
        }

        using FormUrlEncodedContent content = new(formFields);
        using HttpRequestMessage request = new(HttpMethod.Post, "/Account/Register");
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
