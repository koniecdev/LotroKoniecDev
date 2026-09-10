using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Password;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.RateLimiting;

/// <summary>
/// The account pages used to sit outside the limiter while their API twins were inside it, so login,
/// register and password reset were free to hammer (#692). The limit is now the default for the whole
/// Razor group: only POSTs are counted, each page keeps its own budget, and a page that wants something
/// else says so with an attribute.
/// The suite's Testing host keeps the limiter off; these tests turn it on for a derived host, like
/// <see cref="ChangeEmailRateLimitingTests"/>.
/// </summary>
public sealed class AuthPagesRateLimitingTests : EndpointsTestBase
{
    /// <summary>Mirrors the auth-page-limit policy: 10 POSTs per 15 minutes, per page and per client IP.</summary>
    private const int AuthPagePostPermitLimit = 10;

    /// <summary>Mirrors the forgot-password-limit policy the page carries: 3 POSTs per 15 minutes.</summary>
    private const int ForgotPasswordPermitLimit = 3;

    private static readonly Uri LoginPage = new("/Account/Login", UriKind.Relative);
    private static readonly Uri RegisterPage = new("/Account/Register", UriKind.Relative);
    private static readonly Uri ForgotPasswordPage = new("/Account/ForgotPassword", UriKind.Relative);
    private static readonly Uri PrivacyPolicyPage = new("/Account/PrivacyPolicy", UriKind.Relative);
    private static readonly Uri ForgotPasswordEndpoint = new("auth/forgot-password", UriKind.Relative);

    public AuthPagesRateLimitingTests(AuthSystemApiFactory appFactory) : base(appFactory)
    {
    }

    [Theory]
    [InlineData("/Account/Login")]
    [InlineData("/Account/Register")]
    [InlineData("/Account/ResetPassword")]
    public async Task AccountPagePost_ShouldThrottleTheAttemptAfterTheBudget(string page)
    {
        // Arrange: every one of these POSTs costs a PBKDF2 hash. No antiforgery token is sent, so each
        // one ends in 400 — the limiter runs before antiforgery and counts them all the same.
        // ResetPassword carries no attribute of its own, so it proves the group default really applies.
        using WebApplicationFactory<Program> limitedHost = CreateRateLimitedHost();
        using HttpClient client = limitedHost.CreateClient();

        HttpStatusCode[] statusCodes = new HttpStatusCode[AuthPagePostPermitLimit + 1];

        // Act
        for (int i = 0; i < statusCodes.Length; i++)
        {
            using FormUrlEncodedContent attempt = new(new Dictionary<string, string>
            {
                ["Email"] = $"burst-{i}@lotro-translator.pl",
                ["Password"] = "WrongPass1!"
            });
            using HttpResponseMessage response = await client.PostAsync(new Uri(page, UriKind.Relative), attempt);
            statusCodes[i] = response.StatusCode;
        }

        // Assert: antiforgery rejects each of the first ten, so a 500 cannot hide behind "not 429"
        statusCodes.Take(AuthPagePostPermitLimit)
            .ShouldAllBe(statusCode => statusCode == HttpStatusCode.BadRequest);
        statusCodes[AuthPagePostPermitLimit].ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task AccountPagePost_ShouldShareOneBudgetAcrossPathSpellings()
    {
        // Arrange: routing matches a path whatever its casing, so both spellings are the same page. A key
        // built from Request.Path instead of the route template would hand out a budget per spelling.
        using WebApplicationFactory<Program> limitedHost = CreateRateLimitedHost();
        using HttpClient client = limitedHost.CreateClient();

        Uri lowercaseLoginPage = new("/account/login", UriKind.Relative);

        // Act: half the budget under one spelling, the rest plus one under the other
        HttpStatusCode[] statusCodes = new HttpStatusCode[AuthPagePostPermitLimit + 1];
        for (int i = 0; i < statusCodes.Length; i++)
        {
            using FormUrlEncodedContent attempt = new(new Dictionary<string, string>
            {
                ["Email"] = $"burst-{i}@lotro-translator.pl",
                ["Password"] = "WrongPass1!"
            });
            Uri page = i < AuthPagePostPermitLimit / 2 ? LoginPage : lowercaseLoginPage;
            using HttpResponseMessage response = await client.PostAsync(page, attempt);
            statusCodes[i] = response.StatusCode;
        }

        // Assert
        statusCodes.Take(AuthPagePostPermitLimit)
            .ShouldAllBe(statusCode => statusCode != HttpStatusCode.TooManyRequests);
        statusCodes[AuthPagePostPermitLimit].ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task AccountPages_ShouldEachKeepTheirOwnBudget()
    {
        // Arrange: one shared bucket would mean a user who mistyped their password a few times had
        // already spent most of the budget for registering
        using WebApplicationFactory<Program> limitedHost = CreateRateLimitedHost();
        using HttpClient client = limitedHost.CreateClient();

        // Act: spend the login budget in full
        for (int i = 0; i < AuthPagePostPermitLimit + 1; i++)
        {
            using FormUrlEncodedContent attempt = new(new Dictionary<string, string>
            {
                ["Email"] = $"burst-{i}@lotro-translator.pl",
                ["Password"] = "WrongPass1!"
            });
            using HttpResponseMessage response = await client.PostAsync(LoginPage, attempt);
            response.StatusCode.ShouldNotBe(HttpStatusCode.NotFound);
        }

        using FormUrlEncodedContent registration = new(new Dictionary<string, string>
        {
            ["Email"] = "fresh@lotro-translator.pl"
        });
        using HttpResponseMessage registerResponse = await client.PostAsync(RegisterPage, registration);

        // Assert
        registerResponse.StatusCode.ShouldNotBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task LoginPage_ShouldNeverThrottleAPageView()
    {
        // Arrange: the page every sign-in lands on. A view budget would be the one limit real users
        // feel, because a whole mobile carrier can sit behind a single address.
        using WebApplicationFactory<Program> limitedHost = CreateRateLimitedHost();
        using HttpClient client = limitedHost.CreateClient();

        HttpStatusCode[] statusCodes = new HttpStatusCode[AuthPagePostPermitLimit * 3];

        // Act
        for (int i = 0; i < statusCodes.Length; i++)
        {
            using HttpResponseMessage response = await client.GetAsync(LoginPage);
            statusCodes[i] = response.StatusCode;
        }

        // Assert
        statusCodes.ShouldAllBe(statusCode => statusCode != HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task ForgotPasswordPage_ShouldKeepTheStrictSendBudget()
    {
        // Arrange: the page mails whatever address the caller types, so the group default would be far
        // too generous. It carries forgot-password-limit instead, which the group convention must leave
        // alone.
        using WebApplicationFactory<Program> limitedHost = CreateRateLimitedHost();
        using HttpClient client = limitedHost.CreateClient();

        HttpStatusCode[] statusCodes = new HttpStatusCode[ForgotPasswordPermitLimit + 1];

        // Act
        for (int i = 0; i < statusCodes.Length; i++)
        {
            using FormUrlEncodedContent send = new(new Dictionary<string, string>
            {
                ["Email"] = $"burst-{i}@lotro-translator.pl"
            });
            using HttpResponseMessage response = await client.PostAsync(ForgotPasswordPage, send);
            statusCodes[i] = response.StatusCode;
        }

        // Assert
        statusCodes.Take(ForgotPasswordPermitLimit)
            .ShouldAllBe(statusCode => statusCode != HttpStatusCode.TooManyRequests);
        statusCodes[ForgotPasswordPermitLimit].ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task ForgotPasswordPage_ShouldNeverThrottleAPageView()
    {
        // Arrange: a send-sized budget spent on page views would leave the user unable to reach the form
        // at all — the same trap ADR-0046 fixed for the resend page
        using WebApplicationFactory<Program> limitedHost = CreateRateLimitedHost();
        using HttpClient client = limitedHost.CreateClient();

        HttpStatusCode[] statusCodes = new HttpStatusCode[ForgotPasswordPermitLimit * 2 + 1];

        // Act
        for (int i = 0; i < statusCodes.Length; i++)
        {
            using HttpResponseMessage response = await client.GetAsync(ForgotPasswordPage);
            statusCodes[i] = response.StatusCode;
        }

        // Assert
        statusCodes.ShouldAllBe(statusCode => statusCode == HttpStatusCode.OK);
    }

    [Fact]
    public async Task PrivacyPolicyPage_ShouldStayReadable()
    {
        // Arrange: nobody annotated this page and it only ever answers GET. The default must not turn a
        // static document into something a reader can run out of.
        using WebApplicationFactory<Program> limitedHost = CreateRateLimitedHost();
        using HttpClient client = limitedHost.CreateClient();

        HttpStatusCode[] statusCodes = new HttpStatusCode[AuthPagePostPermitLimit * 3];

        // Act
        for (int i = 0; i < statusCodes.Length; i++)
        {
            using HttpResponseMessage response = await client.GetAsync(PrivacyPolicyPage);
            statusCodes[i] = response.StatusCode;
        }

        // Assert
        statusCodes.ShouldAllBe(statusCode => statusCode == HttpStatusCode.OK);
    }

    [Fact]
    public async Task ThrottledPage_ShouldSayWhenToComeBack()
    {
        // Arrange: 429 is the one rejection a caller can act on, so it has to carry Retry-After, and a
        // browser has to get an answer it can read instead of the framework's bare English status text
        using WebApplicationFactory<Program> limitedHost = CreateRateLimitedHost();
        using HttpClient client = limitedHost.CreateClient();

        HttpResponseMessage? lastResponse = null;

        // Act: as a browser would ask, so the rejection is the page and not the machine-readable answer
        for (int i = 0; i < ForgotPasswordPermitLimit + 1; i++)
        {
            lastResponse?.Dispose();
            using FormUrlEncodedContent send = new(new Dictionary<string, string>
            {
                ["Email"] = $"retry-after-{i}@lotro-translator.pl"
            });
            using HttpRequestMessage request = new(HttpMethod.Post, ForgotPasswordPage);
            request.Content = send;
            request.Headers.Accept.ParseAdd("text/html");
            lastResponse = await client.SendAsync(request);
        }

        // Assert
        lastResponse.ShouldNotBeNull();
        lastResponse.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        lastResponse.Headers.RetryAfter.ShouldNotBeNull();
        lastResponse.Headers.RetryAfter!.Delta.ShouldNotBeNull();
        lastResponse.Headers.RetryAfter.Delta!.Value.ShouldBeGreaterThan(TimeSpan.Zero);

        string body = await lastResponse.Content.ReadAsStringAsync();
        body.ShouldContain("Za dużo prób");
        body.ShouldContain("Wróć do logowania");
        lastResponse.Dispose();
    }

    [Fact]
    public async Task ThrottledApiCaller_ShouldNotGetTheBrowserPage()
    {
        // Arrange: the page exists for humans. A machine client — the Frontend's back-channel, the CLI —
        // must keep the answer it can parse, or a throttled token request would come back as HTML.
        using WebApplicationFactory<Program> limitedHost = CreateRateLimitedHost();
        using HttpClient client = limitedHost.CreateClient();

        HttpResponseMessage? lastResponse = null;

        // Act
        for (int i = 0; i < ForgotPasswordPermitLimit + 1; i++)
        {
            lastResponse?.Dispose();
            lastResponse = await client.PostAsJsonAsync(
                ForgotPasswordEndpoint, new ForgotPasswordRequest($"api-burst-{i}@lotro-translator.pl"));
        }

        // Assert
        lastResponse.ShouldNotBeNull();
        lastResponse.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        lastResponse.Content.Headers.ContentType?.MediaType.ShouldNotBe("text/html");

        string body = await lastResponse.Content.ReadAsStringAsync();
        body.ShouldNotContain("<!DOCTYPE html>");
        lastResponse.Dispose();
    }

    private WebApplicationFactory<Program> CreateRateLimitedHost()
    {
        return Factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "RateLimiting:ForceEnable", "true" }
                });
            });
        });
    }
}
