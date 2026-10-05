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
using LotroKoniecDev.AuthSystem.Persistence.Identity;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;
using OpenIddict.Abstractions;

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

    /// <summary>
    /// Each of the revoker's two steps has its own time limit, so this outlasts both.
    /// </summary>
    private static readonly TimeSpan CompletionTimeout = UserSessionRevoker.TimeLimit * 2 + TimeSpan.FromSeconds(10);

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

        using HttpResponseMessage refreshResponse = await RequestRefreshGrantAsync(client, refreshToken);

        // Assert
        refreshResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // The stamp check refuses this refresh on its own (#848), so the row shows that the revoke ran.
        (await OpenIddictTokenState.StatusOfAsync(host.Services, refreshToken)).ShouldBe(OpenIddictConstants.Statuses.Revoked);
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
        (_, string resetToken) = await CreateTokenAsync(
            host.Services,
            user.Email,
            (userManager, account) => userManager.GeneratePasswordResetTokenAsync(account));

        using HttpRequestMessage resetRequest = await CreatePagePostAsync(
            client,
            "/Account/ResetPassword",
            "/Account/ResetPassword",
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

        using HttpResponseMessage refreshResponse = await RequestRefreshGrantAsync(client, refreshToken);

        // Assert
        refreshResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // The stamp check refuses this refresh on its own (#848), so the row shows that the revoke ran.
        (await OpenIddictTokenState.StatusOfAsync(host.Services, refreshToken)).ShouldBe(OpenIddictConstants.Statuses.Revoked);
    }

    [Fact]
    public async Task ConfirmEmailChangePage_ShouldRevokeExistingRefreshTokens_WhenTheRequestIsAbortedAfterTheSave()
    {
        // Arrange
        (RegisterRequest user, _) = await UserFactory.RegisterRandomUserWithRequestAsync(
            ApiClient, Faker, AccountConfirmationEmailSpy, CurrentPassword);
        string newEmail = Faker.Internet.Email(uniqueSuffix: Guid.CreateVersion7().ToString("N"));

        RevocationWatch watch = new();
        await using WebApplicationFactory<Program> host = CreateHostThatAbortsBeforeRevoking(watch);
        using HttpClient client = host.CreateClient();

        (_, string refreshToken) = await SignInAsync(client, user.Email);
        (Guid userId, string changeToken) = await CreateTokenAsync(
            host.Services,
            user.Email,
            (userManager, account) => userManager.GenerateUserTokenAsync(
                account, EmailChangeTokenProvider.ProviderName, EmailChangeTokenProvider.PurposeFor(newEmail)));

        using HttpRequestMessage confirmRequest = await CreatePagePostAsync(
            client,
            "/Account/ConfirmEmailChange",
            $"/Account/ConfirmEmailChange?userId={userId}&email={Uri.EscapeDataString(newEmail)}"
            + $"&token={Uri.EscapeDataString(changeToken)}",
            new Dictionary<string, string>
            {
                ["UserId"] = userId.ToString(),
                ["Email"] = newEmail,
                ["Token"] = changeToken
            });

        // Act: the client sees the abort as an error, so only what the server did afterwards counts
        _ = await Record.ExceptionAsync(() => client.SendAsync(confirmRequest));
        await watch.Finished.Task.WaitAsync(CompletionTimeout);

        using HttpResponseMessage refreshResponse = await RequestRefreshGrantAsync(client, refreshToken);

        // Assert
        refreshResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // The stamp check refuses this refresh on its own (#848), so the row shows that the revoke ran.
        (await OpenIddictTokenState.StatusOfAsync(host.Services, refreshToken)).ShouldBe(OpenIddictConstants.Statuses.Revoked);
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

    /// <summary>
    /// Link tokens are sealed with data protection, and nothing makes two test hosts share a key ring,
    /// so the token comes from the host that will check it.
    /// </summary>
    private static async Task<(Guid UserId, string Token)> CreateTokenAsync(
        IServiceProvider services,
        string email,
        Func<UserManager<ApplicationUser>, ApplicationUser, Task<string>> generate)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        UserManager<ApplicationUser> userManager =
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        ApplicationUser? user = await userManager.FindByEmailAsync(email);
        user.ShouldNotBeNull();

        return (user.Id, await generate(userManager, user));
    }

    private static async Task<HttpRequestMessage> CreatePagePostAsync(
        HttpClient client, string pagePath, string getUrl, Dictionary<string, string> formFields)
    {
        using HttpResponseMessage pageResponse = await client.GetAsync(new Uri(getUrl, UriKind.Relative));
        pageResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        string html = await pageResponse.Content.ReadAsStringAsync();
        Match match = AntiForgeryTokenRegex().Match(html);
        if (match.Success)
        {
            formFields["__RequestVerificationToken"] = match.Groups[1].Value;
        }

        HttpRequestMessage request = new(HttpMethod.Post, pagePath)
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
    }

    /// <summary>
    /// Aborts the request the moment the handler asks for the revoke, then hands the work to the real
    /// revoker. That is the point where a dropped browser used to stop it. If the abort does not take, the
    /// test would pass for the wrong reason, so the watch fails and the test fails in its Act step.
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
            try
            {
                HttpContext httpContext = _httpContextAccessor.HttpContext
                                          ?? throw new InvalidOperationException("The revoke ran outside a request.");

                httpContext.Abort();
                if (!httpContext.RequestAborted.IsCancellationRequested)
                {
                    throw new InvalidOperationException("The request was not aborted before the revoke.");
                }

                await _revoker.RevokeAllAsync(userId);
                _watch.Finished.TrySetResult();
            }
            catch (Exception exception)
            {
                _watch.Finished.TrySetException(exception);
                throw;
            }
        }

        public Task RevokeSessionAsync(string authorizationId) => _revoker.RevokeSessionAsync(authorizationId);
    }
}
