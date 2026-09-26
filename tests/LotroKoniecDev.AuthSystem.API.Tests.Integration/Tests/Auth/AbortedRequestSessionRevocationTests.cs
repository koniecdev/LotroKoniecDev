using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using LotroKoniecDev.AuthSystem.API.BackgroundServices;
using LotroKoniecDev.AuthSystem.API.Services.Sessions;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Password;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

/// <summary>
/// A browser that drops the request right after the save must not keep the other devices signed in
/// (#872). The host aborts the request at exactly that moment, between the committed save and the
/// revoke, which is what a double click, a closed tab or a lost connection does to a real request.
/// </summary>
public sealed partial class AbortedRequestSessionRevocationTests : EndpointsTestBase
{
    private const string CurrentPassword = "TestPass1!";
    private const string NewPassword = "NewPass99!";
    private const string ClientId = "lotrokoniecdev-test";

    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(30);

    public AbortedRequestSessionRevocationTests(AuthSystemApiFactory appFactory) : base(appFactory) { }

    [Fact]
    public async Task ChangePassword_ShouldRevokeExistingRefreshTokens_WhenTheRequestIsAbortedAfterTheSave()
    {
        // Arrange
        (RegisterRequest user, _) = await UserFactory.RegisterRandomUserWithRequestAsync(
            ApiClient, Faker, AccountConfirmationEmailSpy, CurrentPassword);

        RevocationWatch watch = new();
        await using WebApplicationFactory<Program> host = CreateHostThatAbortsBeforeRevoking(watch);
        using HttpClient client = host.CreateClient();

        (string accessToken, string refreshToken) = await SignInAsync(client, user.Email);

        using HttpRequestMessage changeRequest = new(HttpMethod.Post, "auth/change-password");
        changeRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        changeRequest.Content = JsonContent.Create(new ChangePasswordRequest(CurrentPassword, NewPassword));

        // Act: the client sees the abort as an error, so only what the server did afterwards counts
        _ = await Record.ExceptionAsync(() => client.SendAsync(changeRequest));
        await watch.Finished.Task.WaitAsync(CompletionTimeout);

        using HttpResponseMessage refreshResponse = await RefreshAsync(client, refreshToken);

        // Assert
        watch.RequestWasAborted.ShouldBeTrue();
        refreshResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ResetPasswordPage_ShouldRevokeExistingRefreshTokens_WhenTheRequestIsAbortedAfterTheSave()
    {
        // Arrange
        (RegisterRequest user, _) = await UserFactory.RegisterRandomUserWithRequestAsync(
            ApiClient, Faker, AccountConfirmationEmailSpy, CurrentPassword);

        RevocationWatch watch = new();
        await using WebApplicationFactory<Program> host = CreateHostThatAbortsBeforeRevoking(watch);
        using HttpClient client = host.CreateClient();

        (_, string refreshToken) = await SignInAsync(client, user.Email);
        string resetToken = await CreatePasswordResetTokenAsync(host.Services, user.Email);

        using HttpRequestMessage resetRequest = await CreateResetPasswordPagePostAsync(
            client,
            new Dictionary<string, string>
            {
                ["Email"] = user.Email,
                ["Token"] = resetToken,
                ["NewPassword"] = NewPassword,
                ["ConfirmPassword"] = NewPassword
            });

        // Act: the client sees the abort as an error, so only what the server did afterwards counts
        _ = await Record.ExceptionAsync(() => client.SendAsync(resetRequest));
        await watch.Finished.Task.WaitAsync(CompletionTimeout);

        using HttpResponseMessage refreshResponse = await RefreshAsync(client, refreshToken);

        // Assert
        watch.RequestWasAborted.ShouldBeTrue();
        refreshResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private WebApplicationFactory<Program> CreateHostThatAbortsBeforeRevoking(RevocationWatch watch) =>
        Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                // A second relay on this database could take a row another test waits for.
                AuthSystemApiFactory.RemoveHostedService<OutboxRelay>(services);

                services.AddHttpContextAccessor();
                services.AddSingleton(watch);
                services.AddScoped<UserSessionRevoker>();
                services.RemoveAll<IUserSessionRevoker>();
                services.AddScoped<IUserSessionRevoker, RequestAbortingSessionRevoker>();
            }));

    /// <summary>
    /// Tokens are signed and sealed with this host's own keys, so they come from the host that will
    /// check them.
    /// </summary>
    private static async Task<(string AccessToken, string RefreshToken)> SignInAsync(HttpClient client, string email)
    {
        using FormUrlEncodedContent loginRequest = new(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = email,
            ["password"] = CurrentPassword,
            ["client_id"] = ClientId,
            ["scope"] = "email profile roles api offline_access"
        });

        using HttpResponseMessage loginResponse = await client.PostAsync(
            new Uri("connect/token", UriKind.Relative), loginRequest);
        loginResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        string content = await loginResponse.Content.ReadAsStringAsync();
        using JsonDocument json = JsonDocument.Parse(content);

        return (
            json.RootElement.GetProperty("access_token").GetString()!,
            json.RootElement.GetProperty("refresh_token").GetString()!);
    }

    private static async Task<HttpResponseMessage> RefreshAsync(HttpClient client, string refreshToken)
    {
        using FormUrlEncodedContent refreshRequest = new(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = ClientId
        });

        return await client.PostAsync(new Uri("connect/token", UriKind.Relative), refreshRequest);
    }

    /// <summary>
    /// The reset token is sealed with data protection, and nothing makes two test hosts share a key
    /// ring, so the token comes from the host that will check it.
    /// </summary>
    private static async Task<string> CreatePasswordResetTokenAsync(IServiceProvider services, string email)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        UserManager<ApplicationUser> userManager =
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        ApplicationUser? user = await userManager.FindByEmailAsync(email);
        user.ShouldNotBeNull();

        return await userManager.GeneratePasswordResetTokenAsync(user);
    }

    private static async Task<HttpRequestMessage> CreateResetPasswordPagePostAsync(
        HttpClient client, Dictionary<string, string> formFields)
    {
        using HttpResponseMessage pageResponse = await client.GetAsync(
            new Uri("/Account/ResetPassword", UriKind.Relative));
        pageResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        string html = await pageResponse.Content.ReadAsStringAsync();
        Match match = AntiForgeryTokenRegex().Match(html);
        if (match.Success)
        {
            formFields["__RequestVerificationToken"] = match.Groups[1].Value;
        }

        HttpRequestMessage request = new(HttpMethod.Post, "/Account/ResetPassword")
        {
            Content = new FormUrlEncodedContent(formFields)
        };

        if (pageResponse.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? cookies))
        {
            foreach (string cookie in cookies)
            {
                request.Headers.Add("Cookie", cookie.Split(';')[0]);
            }
        }

        return request;
    }

    [GeneratedRegex("""name="__RequestVerificationToken".*?value="([^"]+)""")]
    private static partial Regex AntiForgeryTokenRegex();

    private sealed class RevocationWatch
    {
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool RequestWasAborted { get; set; }
    }

    /// <summary>
    /// Aborts the request the moment the handler asks for the revoke, then hands the work to the real
    /// revoker. That is the point where a dropped browser used to stop it.
    /// </summary>
    private sealed class RequestAbortingSessionRevoker : IUserSessionRevoker
    {
        private readonly UserSessionRevoker _revoker;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly RevocationWatch _watch;

        public RequestAbortingSessionRevoker(
            UserSessionRevoker revoker,
            IHttpContextAccessor httpContextAccessor,
            RevocationWatch watch)
        {
            _revoker = revoker;
            _httpContextAccessor = httpContextAccessor;
            _watch = watch;
        }

        public async Task RevokeAllAsync(string userId)
        {
            HttpContext httpContext = _httpContextAccessor.HttpContext
                                      ?? throw new InvalidOperationException("The revoke ran outside a request.");

            httpContext.Abort();
            _watch.RequestWasAborted = httpContext.RequestAborted.IsCancellationRequested;

            try
            {
                await _revoker.RevokeAllAsync(userId);
            }
            finally
            {
                _watch.Finished.TrySetResult();
            }
        }
    }
}
