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
        await AccountConfirmationEmailSpy.WaitForCaptureAsync();
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
    public async Task RegisterPage_ShouldShowError_WhenTermsOfServiceNotAccepted()
    {
        // Arrange
        AccountConfirmationEmailSpy.Reset();
        RegisterRequest request = UserFactory.GenerateRandomRegisterRequest(Faker);

        // Act
        HttpResponseMessage response = await PostToRegisterPageAsync(
            BuildForm(request, request.Password, acceptedTermsOfService: false));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        string html = await response.Content.ReadAsStringAsync();
        html.ShouldContain("Musisz zaakceptować regulamin serwisu");
        AccountConfirmationEmailSpy.LastEmail.ShouldBeNull();
    }

    [Fact]
    public async Task RegisterPage_ShouldRenderTermsConsentWithLink_WhenAccessed()
    {
        // Act
        HttpResponseMessage response = await ApiClient.Http.GetAsync(
            new Uri("/Account/Register", UriKind.Relative));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        string html = await response.Content.ReadAsStringAsync();
        html.ShouldContain("register-accept-terms");
        // The terms URL comes from the web client's first post-logout redirect URI, which
        // AuthSystemApiFactory sets to https://localhost:5001. If it fell back to the version without a
        // link, the consent checkbox would point at terms the person registering cannot open.
        html.ShouldContain("""<a href="https://localhost:5001/regulamin" target="_blank" rel="noopener">regulamin serwisu</a>""");
    }

    [Theory]
    [InlineData("kasia 92")]
    [InlineData("kasia.92")]
    [InlineData("kasia_92")]
    [InlineData("kasia@92")]
    [InlineData("kaśka92")]
    public async Task RegisterPage_ShouldShowCharsetError_WhenUsernameHasIllegalCharacters(string username)
    {
        // Arrange
        AccountConfirmationEmailSpy.Reset();
        RegisterRequest request = UserFactory.GenerateRandomRegisterRequest(Faker);
        Dictionary<string, string> form = BuildForm(request, request.Password);
        form["Username"] = username;

        // Act
        HttpResponseMessage response = await PostToRegisterPageAsync(form);

        // Assert: the explicit Polish charset rule, never the misleading password hint
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        string html = await response.Content.ReadAsStringAsync();
        html.ShouldContain("Nazwa użytkownika może zawierać tylko litery i cyfry, bez spacji.");
        html.ShouldNotContain("hasło spełnia wymagania");
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

    /// <summary>
    /// The register half of #681. The target is a valid local path even with a quote in it, so the check
    /// keeps it and the page prints it; the encoding of the attribute is what makes it harmless, which is
    /// why the proof has to read the rendered body.
    /// </summary>
    [Theory]
    [InlineData("""/x" onfocus="alert(1)""", "/x&quot; onfocus=&quot;alert(1)")]
    [InlineData("/x<script>alert(1)</script>", "/x&lt;script&gt;alert(1)&lt;/script&gt;")]
    public async Task RegisterPage_ShouldEncodeTheReturnUrl_WhenALocalPathCarriesHtmlCharacters(
        string returnUrl,
        string expectedAttributeValue)
    {
        // Act
        HttpResponseMessage response = await GetRegisterPageAsync(returnUrl);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        string html = await response.Content.ReadAsStringAsync();
        html.ShouldContain($"""<input type="hidden" name="returnUrl" value="{expectedAttributeValue}" />""");
        html.ShouldNotContain(returnUrl);
    }

    /// <summary>
    /// Dropping the tag-helper route value also dropped the generated action attribute, and the form tag
    /// helper decides on the antiforgery token from exactly that: it only leaves the token out when the
    /// markup sets an action itself. Without the token every registration POST would answer 400.
    /// </summary>
    [Fact]
    public async Task RegisterPage_ShouldStillEmitTheAntiforgeryToken_WhenTheFormCarriesNoAction()
    {
        // Act
        HttpResponseMessage response = await GetRegisterPageAsync("/connect/authorize?client_id=web");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        string html = await response.Content.ReadAsStringAsync();
        ExtractAntiForgeryToken(html).ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// Registration interrupts an authorization request, and the "log in" button on the success screen is
    /// what resumes it. Since #681 only the hidden field carries that target across the POST, so the form
    /// goes to the bare page path and the link is read back from the answer.
    /// </summary>
    [Fact]
    public async Task RegisterPage_ShouldKeepTheContinuationOnTheLoginLink_WhenOnlyTheRenderedFormCarriesIt()
    {
        // Arrange
        const string continuation = "/connect/authorize?client_id=lotrokoniecdev-test&response_type=code";
        AccountConfirmationEmailSpy.Reset();
        RegisterRequest request = UserFactory.GenerateRandomRegisterRequest(Faker);

        HttpResponseMessage pageResponse = await GetRegisterPageAsync(continuation);
        pageResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        string renderedPage = await pageResponse.Content.ReadAsStringAsync();

        Match hiddenField = HiddenReturnUrlRegex().Match(renderedPage);
        hiddenField.Success.ShouldBeTrue("The register form must carry the return target in a hidden field.");

        Dictionary<string, string> formFields = BuildForm(request, request.Password);
        formFields["returnUrl"] = WebUtility.HtmlDecode(hiddenField.Groups[1].Value);

        // Act
        HttpResponseMessage response = await PostRegisterFormAsync(pageResponse, renderedPage, formFields);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        string html = await response.Content.ReadAsStringAsync();
        html.ShouldContain("Konto zostało utworzone");
        html.ShouldContain($"/Account/Login?returnUrl={Uri.EscapeDataString(continuation)}");
    }

    private static Dictionary<string, string> BuildForm(
        RegisterRequest request,
        string confirmPassword,
        bool acceptedPrivacyPolicy = true,
        bool acceptedDataProcessingConsent = true,
        bool acceptedTermsOfService = true) => new()
        {
            ["Username"] = request.Username,
            ["Email"] = request.Email,
            ["Password"] = request.Password,
            ["ConfirmPassword"] = confirmPassword,
            ["AcceptedPrivacyPolicy"] = acceptedPrivacyPolicy ? "true" : "false",
            ["AcceptedDataProcessingConsent"] = acceptedDataProcessingConsent ? "true" : "false",
            ["AcceptedTermsOfService"] = acceptedTermsOfService ? "true" : "false"
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

    private async Task<HttpResponseMessage> GetRegisterPageAsync(string returnUrl) =>
        await ApiClient.Http.GetAsync(new Uri(
            $"/Account/Register?returnUrl={Uri.EscapeDataString(returnUrl)}", UriKind.Relative));

    /// <summary>
    /// Posts the given fields to the bare page path, carrying the antiforgery cookie and token from the
    /// page that rendered them. That path is where a test sends the form when the hidden field alone has
    /// to carry the target: a browser would post to the current address and repeat the query string.
    /// </summary>
    private async Task<HttpResponseMessage> PostRegisterFormAsync(
        HttpResponseMessage pageResponse,
        string renderedPage,
        Dictionary<string, string> formFields)
    {
        if (ExtractAntiForgeryToken(renderedPage) is { } antiForgeryToken)
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

    [GeneratedRegex("""name="returnUrl"[^>]*value="([^">]*)""")]
    private static partial Regex HiddenReturnUrlRegex();
}
