using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Password;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.AuthSystem.Persistence.Outbox;
using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

/// <summary>
/// Cross-endpoint behavior of the 14-day deletion grace window (ADR-0031): the hosted
/// login page reveals the scheduled state only to the password holder, the password
/// flows cannot bypass the window, tokens cannot be refreshed, and the emailed cancel
/// link drives the hosted recovery flow.
/// </summary>
[Collection("AuthApi")]
public sealed partial class DeletionGraceWindowTests : AsyncLifetimeTestBase
{
    private const string TestPassword = "TestPass1!";

    protected override TestApiClient ApiClient { get; }

    private readonly HttpClient _noRedirectClient;

    public DeletionGraceWindowTests(AuthSystemApiFactory appFactory) : base(appFactory)
    {
        JsonSerializerOptions jsonSerializerOptions =
            appFactory.Services.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;

        ApiClient = new TestApiClient(appFactory.CreateClient(), jsonSerializerOptions);

        _noRedirectClient = appFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    [Fact]
    public async Task LoginPage_ShouldShowScheduledDeletionMessage_WhenPasswordIsCorrect()
    {
        // Arrange
        (RegisterRequest registerRequest, _) = await RegisterAndScheduleDeletionAsync();

        // Act
        HttpResponseMessage response = await PostLoginFormAsync(registerRequest.Email, TestPassword);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        string html = await response.Content.ReadAsStringAsync();
        html.ShouldContain("zaplanowane do usunięcia");
    }

    [Fact]
    public async Task LoginPage_ShouldShowGenericError_WhenPasswordIsWrongDuringGraceWindow()
    {
        // Arrange
        (RegisterRequest registerRequest, _) = await RegisterAndScheduleDeletionAsync();

        // Act
        HttpResponseMessage response = await PostLoginFormAsync(registerRequest.Email, "WrongPassword1!");

        // Assert: the scheduled state must not leak without the correct password
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        string html = await response.Content.ReadAsStringAsync();
        html.ShouldContain("Nieprawidłowy e-mail lub hasło");
        html.ShouldNotContain("zaplanowane do usunięcia");
    }

    [Fact]
    public async Task ForgotPassword_ShouldPretendSuccessWithoutSendingEmail_DuringGraceWindow()
    {
        // Arrange
        (RegisterRequest registerRequest, _) = await RegisterAndScheduleDeletionAsync();
        PasswordResetEmailSpy.Reset();

        // Act
        HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/forgot-password", UriKind.Relative),
            new ForgotPasswordRequest(registerRequest.Email));

        // Assert: the response says success, so nobody can find out which accounts exist, but the reset
        // e-mail must not go out. The request only commits an outbox row (ADR-0038) and the check for a
        // scheduled deletion happens in the dispatch processor. So wait until the delivery was really
        // handled, which the inbox row tells us. Asserting right after the POST could let a broken check
        // pass as green.
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        OutboxMessage? outboxRow = await OutboxAssertions.WaitForOutboxRowAsync(
            Factory, row => row.Type == nameof(PasswordResetRequested));
        outboxRow.ShouldNotBeNull();
        (await OutboxAssertions.WaitForInboxRowsAsync(Factory, outboxRow.Id)).ShouldBe(1);
        PasswordResetEmailSpy.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task ResetPassword_ShouldRejectPreIssuedToken_DuringGraceWindow()
    {
        // Arrange: obtain a reset token BEFORE scheduling the deletion
        (RegisterRequest registerRequest, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, TestPassword);

        PasswordResetEmailSpy.Reset();
        HttpResponseMessage forgotResponse = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/forgot-password", UriKind.Relative),
            new ForgotPasswordRequest(registerRequest.Email));
        forgotResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        await PasswordResetEmailSpy.WaitForCaptureAsync();
        string preIssuedResetToken = PasswordResetEmailSpy.LastResetToken!;

        await ScheduleDeletionAsync(registerRequest.Email);

        // Act: the pre-issued token must not restore access during the grace window
        HttpResponseMessage resetResponse = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/reset-password", UriKind.Relative),
            new ResetPasswordRequest(registerRequest.Email, preIssuedResetToken, "BrandNewPass1!"));

        // Assert
        resetResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        string body = await resetResponse.Content.ReadAsStringAsync();
        body.ShouldContain("Auth.InvalidPasswordResetToken");
    }

    [Fact]
    public async Task ChangePassword_ShouldBeBlockedAndPreserveCancelToken_DuringGraceWindow()
    {
        // Arrange: the ADR-0031 threat: the attacker holds a pre-schedule access token
        // (self-contained JWTs stay valid for their TTL) plus the current password, and
        // tries to rotate the security stamp the emailed cancel token is bound to
        (RegisterRequest registerRequest, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, TestPassword);
        string preScheduleAccessToken = await GetAccessTokenAsync(registerRequest.Email);
        await ScheduleDeletionAsync(registerRequest.Email);
        string cancelToken = AccountDeletionEmailSpy.LastCancelToken!;

        // The cancel token above was created at delivery time, against the stamp set when the deletion
        // was scheduled (ADR-0038 decision 2). The attack below must not be able to invalidate it.

        ChangePasswordRequest changeRequest = new(TestPassword, "AttackerNewPass1!");
        using HttpRequestMessage request = new(HttpMethod.Post, "auth/change-password");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", preScheduleAccessToken);
        request.Content = JsonContent.Create(changeRequest);

        // Act
        HttpResponseMessage response = await ApiClient.Http.SendAsync(request);

        // Assert: blocked, and the emailed cancel link still works afterwards
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        string body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("Auth.DeletionAlreadyScheduled");

        HttpResponseMessage cancelResponse = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/account/cancel-deletion", UriKind.Relative),
            new CancelAccountDeletionRequest(registerRequest.Email, cancelToken));
        cancelResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task RefreshGrant_ShouldRejectRefreshToken_DuringGraceWindow()
    {
        // Arrange: a refresh token issued BEFORE scheduling; revocation on schedule is
        // best-effort, so the refresh-grant gate is the guarantee
        (RegisterRequest registerRequest, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, TestPassword);

        using FormUrlEncodedContent passwordGrant = new(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = registerRequest.Email,
            ["password"] = TestPassword,
            ["client_id"] = "lotrokoniecdev-test",
            ["scope"] = "email profile roles api offline_access"
        });
        HttpResponseMessage loginResponse = await ApiClient.Http.PostAsync(
            new Uri("connect/token", UriKind.Relative), passwordGrant);
        loginResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        string loginContent = await loginResponse.Content.ReadAsStringAsync();
        string refreshToken;
        using (JsonDocument loginJson = JsonDocument.Parse(loginContent))
        {
            refreshToken = loginJson.RootElement.GetProperty("refresh_token").GetString()!;
        }

        await ScheduleDeletionAsync(registerRequest.Email);

        using FormUrlEncodedContent refreshGrant = new(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = "lotrokoniecdev-test"
        });

        // Act
        HttpResponseMessage refreshResponse = await ApiClient.Http.PostAsync(
            new Uri("connect/token", UriKind.Relative), refreshGrant);

        // Assert: a scheduled account must not refresh its way back to a usable token
        refreshResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CancelDeletionPage_ShouldRenderConfirmationForm_OnGet()
    {
        // Arrange
        (RegisterRequest registerRequest, _) = await RegisterAndScheduleDeletionAsync();
        string cancelToken = AccountDeletionEmailSpy.LastCancelToken!;

        // Act: a GET (e.g. a mail scanner prefetch) must NOT cancel anything
        HttpResponseMessage response = await _noRedirectClient.GetAsync(new Uri(
            $"/Account/CancelDeletion?email={Uri.EscapeDataString(registerRequest.Email)}&token={Uri.EscapeDataString(cancelToken)}",
            UriKind.Relative));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        string html = await response.Content.ReadAsStringAsync();
        html.ShouldContain("cancel-deletion-form");

        HttpResponseMessage loginResponse = await PostLoginFormAsync(registerRequest.Email, TestPassword);
        string loginHtml = await loginResponse.Content.ReadAsStringAsync();
        loginHtml.ShouldContain("zaplanowane do usunięcia");
    }

    [Fact]
    public async Task CancelDeletionPage_ShouldCancelAndRedirectToPasswordReset_OnPost()
    {
        // Arrange
        (RegisterRequest registerRequest, _) = await RegisterAndScheduleDeletionAsync();
        string cancelToken = AccountDeletionEmailSpy.LastCancelToken!;

        string pageUrl =
            $"/Account/CancelDeletion?email={Uri.EscapeDataString(registerRequest.Email)}&token={Uri.EscapeDataString(cancelToken)}";
        HttpResponseMessage pageResponse = await _noRedirectClient.GetAsync(new Uri(pageUrl, UriKind.Relative));
        pageResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        string pageHtml = await pageResponse.Content.ReadAsStringAsync();
        string? antiForgeryToken = ExtractAntiForgeryToken(pageHtml);

        Dictionary<string, string> formData = new()
        {
            ["Email"] = registerRequest.Email,
            ["Token"] = cancelToken
        };
        if (antiForgeryToken is not null)
        {
            formData["__RequestVerificationToken"] = antiForgeryToken;
        }

        using FormUrlEncodedContent content = new(formData);
        using HttpRequestMessage postRequest = new(HttpMethod.Post, pageUrl);
        postRequest.Content = content;
        if (pageResponse.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? cookies))
        {
            foreach (string cookie in cookies)
            {
                postRequest.Headers.Add("Cookie", cookie.Split(';')[0]);
            }
        }

        // Act
        HttpResponseMessage response = await _noRedirectClient.SendAsync(postRequest);

        // Assert: the page hands the user straight into the forced password reset
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        string? location = response.Headers.Location?.ToString();
        location.ShouldNotBeNull();
        location.ShouldContain("/Account/ResetPassword");
        location.ShouldContain("token=");
    }

    private async Task<(RegisterRequest Request, string CancelToken)> RegisterAndScheduleDeletionAsync()
    {
        (RegisterRequest registerRequest, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, TestPassword);

        await ScheduleDeletionAsync(registerRequest.Email);

        return (registerRequest, AccountDeletionEmailSpy.LastCancelToken!);
    }

    private async Task ScheduleDeletionAsync(string email)
    {
        string accessToken = await GetAccessTokenAsync(email);

        DeleteAccountRequest deleteRequest = new(TestPassword);
        using HttpRequestMessage request = new(HttpMethod.Post, "auth/account/delete");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(deleteRequest);

        HttpResponseMessage response = await ApiClient.Http.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // The cancel token arrives through the pipeline (ADR-0038) and not with the request. Callers
        // read it off the spy right after this returns, so wait for the delivery here.
        await AccountDeletionEmailSpy.WaitForScheduledCaptureAsync();
    }

    private async Task<string> GetAccessTokenAsync(string email)
    {
        using FormUrlEncodedContent tokenRequest = new(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = email,
            ["password"] = TestPassword,
            ["client_id"] = "lotrokoniecdev-test",
            ["scope"] = "email profile roles api"
        });

        HttpResponseMessage tokenResponse = await ApiClient.Http.PostAsync(
            new Uri("connect/token", UriKind.Relative), tokenRequest);
        tokenResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        string content = await tokenResponse.Content.ReadAsStringAsync();
        using JsonDocument json = JsonDocument.Parse(content);
        return json.RootElement.GetProperty("access_token").GetString()!;
    }

    private async Task<HttpResponseMessage> PostLoginFormAsync(string email, string password)
    {
        HttpResponseMessage loginPageResponse = await _noRedirectClient.GetAsync(
            new Uri("/Account/Login", UriKind.Relative));
        loginPageResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        string loginPageHtml = await loginPageResponse.Content.ReadAsStringAsync();
        string? antiForgeryToken = ExtractAntiForgeryToken(loginPageHtml);

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
        using HttpRequestMessage request = new(HttpMethod.Post, "/Account/Login");
        request.Content = content;
        if (loginPageResponse.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? cookies))
        {
            foreach (string cookie in cookies)
            {
                request.Headers.Add("Cookie", cookie.Split(';')[0]);
            }
        }

        return await _noRedirectClient.SendAsync(request);
    }

    private static string? ExtractAntiForgeryToken(string html)
    {
        Match match = AntiForgeryTokenRegex().Match(html);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex("""name="__RequestVerificationToken".*?value="([^"]+)""")]
    private static partial Regex AntiForgeryTokenRegex();
}
