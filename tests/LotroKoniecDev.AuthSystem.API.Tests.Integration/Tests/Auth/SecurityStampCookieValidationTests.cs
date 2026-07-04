using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Password;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

[Collection("AuthApi")]
public sealed partial class SecurityStampCookieValidationTests : AsyncLifetimeTestBase
{
    protected override TestApiClient ApiClient { get; }

    private readonly HttpClient _noRedirectClient;

    public SecurityStampCookieValidationTests(AuthSystemApiFactory appFactory) : base(appFactory)
    {
        JsonSerializerOptions jsonSerializerOptions =
            appFactory.Services.GetRequiredService<IOptionsSnapshot<JsonOptions>>().Value.SerializerOptions;

        ApiClient = new TestApiClient(appFactory.CreateClient(), jsonSerializerOptions);

        // Cookies are managed by hand so the test controls exactly which cookie rides each request.
        _noRedirectClient = appFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });
    }

    [Fact]
    public async Task Authorize_WithLiveCookie_IsBouncedToLogin_AfterPasswordResetRotatesSecurityStamp()
    {
        // Arrange — a confirmed user with an established interactive auth-server cookie.
        const string originalPassword = "TestPass1!";
        const string newPassword = "NewPass99!";

        (RegisterRequest registerRequest, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, originalPassword);

        List<string> authCookies = await EstablishAuthCookieAsync(registerRequest.Email, originalPassword);

        // Baseline — before the stamp changes, the live cookie authenticates /connect/authorize and an
        // authorization code is minted (proves the cookie is genuinely usable, so the post-reset
        // rejection below cannot pass for the wrong reason).
        (HttpStatusCode beforeStatus, string beforeLocation) = await AuthorizeWithCookieAsync(authCookies);
        beforeStatus.ShouldBe(HttpStatusCode.Redirect);
        beforeLocation.ShouldContain("code=");
        beforeLocation.ShouldNotContain("/Account/Login");

        // Act — reset the password through the browser page; this rotates the Identity security stamp.
        string resetHtml = await ResetPasswordAsync(registerRequest.Email, newPassword);
        resetHtml.ShouldContain("Hasło zmienione"); // reset completed → the security stamp rotated

        // Assert — the SAME cookie can no longer complete /connect/authorize; it is bounced to login.
        (HttpStatusCode afterStatus, string afterLocation) = await AuthorizeWithCookieAsync(authCookies);
        afterStatus.ShouldBe(HttpStatusCode.Redirect);
        afterLocation.ShouldContain("/Account/Login");
    }

    private async Task<List<string>> EstablishAuthCookieAsync(string email, string password)
    {
        HttpResponseMessage loginPage = await _noRedirectClient.GetAsync(
            new Uri("/Account/Login", UriKind.Relative));
        loginPage.StatusCode.ShouldBe(HttpStatusCode.OK);

        string html = await loginPage.Content.ReadAsStringAsync();
        string? antiForgeryToken = ExtractAntiForgeryToken(html);

        Dictionary<string, string> loginFormData = new()
        {
            ["Email"] = email,
            ["Password"] = password
        };

        if (antiForgeryToken is not null)
        {
            loginFormData["__RequestVerificationToken"] = antiForgeryToken;
        }

        using FormUrlEncodedContent content = new(loginFormData);
        using HttpRequestMessage request = new(HttpMethod.Post, "/Account/Login") { Content = content };

        foreach (string cookie in loginPage.Headers.GetValues("Set-Cookie"))
        {
            request.Headers.Add("Cookie", cookie.Split(';')[0]);
        }

        HttpResponseMessage loginResponse = await _noRedirectClient.SendAsync(request);

        // A successful sign-in redirects (302); a rejected one re-renders the page (200).
        loginResponse.StatusCode.ShouldBe(HttpStatusCode.Redirect);

        List<string> authCookies = loginResponse.Headers
            .Where(header => header.Key == "Set-Cookie")
            .SelectMany(header => header.Value)
            .ToList();

        authCookies.ShouldNotBeEmpty();
        return authCookies;
    }

    private async Task<(HttpStatusCode Status, string Location)> AuthorizeWithCookieAsync(List<string> authCookies)
    {
        (_, string codeChallenge) = GeneratePkce();
        string authorizeUrl = BuildAuthorizeUrl(codeChallenge);

        using HttpRequestMessage request = new(HttpMethod.Get, authorizeUrl);
        foreach (string cookie in authCookies)
        {
            request.Headers.Add("Cookie", cookie.Split(';')[0]);
        }

        HttpResponseMessage response = await _noRedirectClient.SendAsync(request);
        string location = response.Headers.Location?.ToString() ?? string.Empty;
        return (response.StatusCode, location);
    }

    private async Task<string> ResetPasswordAsync(string email, string newPassword)
    {
        PasswordResetEmailSpy.Reset();
        await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/forgot-password", UriKind.Relative),
            new ForgotPasswordRequest(email));
        string resetToken = PasswordResetEmailSpy.LastResetToken!;

        HttpResponseMessage resetResponse = await PostToResetPasswordPageAsync(new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Token"] = resetToken,
            ["NewPassword"] = newPassword,
            ["ConfirmPassword"] = newPassword
        });

        resetResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await resetResponse.Content.ReadAsStringAsync();
    }

    private async Task<HttpResponseMessage> PostToResetPasswordPageAsync(Dictionary<string, string> formFields)
    {
        HttpResponseMessage pageResponse = await ApiClient.Http.GetAsync(
            new Uri("/Account/ResetPassword", UriKind.Relative));
        pageResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        string html = await pageResponse.Content.ReadAsStringAsync();
        string? antiForgeryToken = ExtractAntiForgeryToken(html);
        if (antiForgeryToken is not null)
        {
            formFields["__RequestVerificationToken"] = antiForgeryToken;
        }

        using FormUrlEncodedContent content = new(formFields);
        using HttpRequestMessage request = new(HttpMethod.Post, "/Account/ResetPassword") { Content = content };

        if (pageResponse.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? cookies))
        {
            foreach (string cookie in cookies)
            {
                request.Headers.Add("Cookie", cookie.Split(';')[0]);
            }
        }

        return await ApiClient.Http.SendAsync(request);
    }

    private static string BuildAuthorizeUrl(string codeChallenge) =>
        "connect/authorize?response_type=code" +
        "&client_id=lotrokoniecdev-web" +
        $"&redirect_uri={Uri.EscapeDataString("https://localhost:5001/callback")}" +
        $"&scope={Uri.EscapeDataString("openid email profile roles api offline_access")}" +
        $"&code_challenge={codeChallenge}" +
        "&code_challenge_method=S256";

    private static (string CodeVerifier, string CodeChallenge) GeneratePkce()
    {
        byte[] randomBytes = new byte[32];
        using RandomNumberGenerator rng = RandomNumberGenerator.Create();
        rng.GetBytes(randomBytes);
        string codeVerifier = Convert.ToBase64String(randomBytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        byte[] challengeBytes = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        string codeChallenge = Convert.ToBase64String(challengeBytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        return (codeVerifier, codeChallenge);
    }

    private static string? ExtractAntiForgeryToken(string html)
    {
        Match match = AntiForgeryTokenRegex().Match(html);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex("""name="__RequestVerificationToken".*?value="([^"]+)""")]
    private static partial Regex AntiForgeryTokenRegex();
}
