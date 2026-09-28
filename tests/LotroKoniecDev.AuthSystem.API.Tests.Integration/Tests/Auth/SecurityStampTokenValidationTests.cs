using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using LotroKoniecDev.AuthSystem.API.BackgroundServices;
using LotroKoniecDev.AuthSystem.API.Services.Sessions;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Password;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.Identity;
using OpenIddict.Abstractions;
using OpenIddict.Server;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

/// <summary>
/// A refresh token dies with the security stamp it was issued under (#848). Revoking sessions is only best
/// effort, and cancelling a deletion changes the stamp without revoking anything. So the flow tests run on
/// a host whose revoke does nothing, and only the stamp check is left to refuse the refresh.
/// </summary>
public sealed partial class SecurityStampTokenValidationTests : EndpointsTestBase
{
    private const string CurrentPassword = "TestPass1!";
    private const string NewPassword = "NewPass99!";
    private const string ClientId = "lotrokoniecdev-test";
    private const string OfflineScopes = "email profile roles api offline_access";

    public SecurityStampTokenValidationTests(AuthSystemApiFactory appFactory) : base(appFactory) { }

    [Fact]
    public async Task RefreshTokenGrant_ShouldFail_WhenSecurityStampChangedAfterSignIn()
    {
        // Arrange
        (RegisterRequest user, _) = await UserFactory.RegisterRandomUserWithRequestAsync(
            ApiClient, Faker, AccountConfirmationEmailSpy, CurrentPassword);
        (_, string refreshToken, _) = await SignInAsync(ApiClient.Http, user.Email, OfflineScopes);

        await using (AsyncServiceScope scope = Factory.Services.CreateAsyncScope())
        {
            UserManager<ApplicationUser> userManager =
                scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            ApplicationUser? account = await userManager.FindByEmailAsync(user.Email);
            account.ShouldNotBeNull();
            (await userManager.UpdateSecurityStampAsync(account)).Succeeded.ShouldBeTrue();
        }

        // The row is still valid, so only the stamp check can refuse the refresh below.
        (await OpenIddictTokenState.StatusOfAsync(Factory.Services, refreshToken)).ShouldBe(OpenIddictConstants.Statuses.Valid);

        // Act
        using HttpResponseMessage response = await RefreshAsync(ApiClient.Http, refreshToken);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("invalid_grant");
    }

    [Fact]
    public async Task RefreshTokenGrant_ShouldKeepWorking_WhenSecurityStampIsUnchanged()
    {
        // Arrange: every refresh hands out a new refresh token. If that one lost the stamp, the second
        // refresh would fail and every user would be signed out about ten minutes after signing in.
        (RegisterRequest user, _) = await UserFactory.RegisterRandomUserWithRequestAsync(
            ApiClient, Faker, AccountConfirmationEmailSpy, CurrentPassword);
        (_, string firstRefreshToken, _) = await SignInAsync(ApiClient.Http, user.Email, OfflineScopes);

        using HttpResponseMessage firstRefresh = await RefreshAsync(ApiClient.Http, firstRefreshToken);
        firstRefresh.StatusCode.ShouldBe(HttpStatusCode.OK);
        string secondRefreshToken = await ReadTokenAsync(firstRefresh, "refresh_token");

        // Act
        using HttpResponseMessage response = await RefreshAsync(ApiClient.Http, secondRefreshToken);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task RefreshTokenGrant_ShouldFail_AfterACancelledDeletionWhoseRevocationDidNotRun()
    {
        // Arrange
        (RegisterRequest user, _) = await UserFactory.RegisterRandomUserWithRequestAsync(
            ApiClient, Faker, AccountConfirmationEmailSpy, CurrentPassword);

        await using WebApplicationFactory<Program> host = CreateHostThatNeverRevokes();
        using HttpClient client = host.CreateClient();

        (string accessToken, string refreshToken, _) = await SignInAsync(client, user.Email, OfflineScopes);

        using HttpRequestMessage deleteRequest = new(HttpMethod.Post, "auth/account/delete");
        deleteRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        deleteRequest.Content = JsonContent.Create(new DeleteAccountRequest(CurrentPassword));
        using HttpResponseMessage deleteResponse = await client.SendAsync(deleteRequest);
        deleteResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        string cancelToken = await CreateCancelTokenAsync(host.Services, user.Email);
        using HttpResponseMessage cancelResponse = await client.PostAsJsonAsync(
            new Uri("auth/account/cancel-deletion", UriKind.Relative),
            new CancelAccountDeletionRequest(user.Email, cancelToken));
        cancelResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        // The row is still valid, so only the stamp check can refuse the refresh below.
        (await OpenIddictTokenState.StatusOfAsync(host.Services, refreshToken)).ShouldBe(OpenIddictConstants.Statuses.Valid);

        // Act: the account is no longer locked or waiting for deletion
        using HttpResponseMessage response = await RefreshAsync(client, refreshToken);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("invalid_grant");
    }

    [Fact]
    public async Task RefreshTokenGrant_ShouldFail_AfterAPasswordChangeWhoseRevocationDidNotRun()
    {
        // Arrange
        (RegisterRequest user, _) = await UserFactory.RegisterRandomUserWithRequestAsync(
            ApiClient, Faker, AccountConfirmationEmailSpy, CurrentPassword);

        await using WebApplicationFactory<Program> host = CreateHostThatNeverRevokes();
        using HttpClient client = host.CreateClient();

        (string accessToken, string refreshToken, _) = await SignInAsync(client, user.Email, OfflineScopes);

        using HttpRequestMessage changeRequest = new(HttpMethod.Post, "auth/change-password");
        changeRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        changeRequest.Content = JsonContent.Create(new ChangePasswordRequest(CurrentPassword, NewPassword));
        using HttpResponseMessage changeResponse = await client.SendAsync(changeRequest);
        changeResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        // The row is still valid, so only the stamp check can refuse the refresh below.
        (await OpenIddictTokenState.StatusOfAsync(host.Services, refreshToken)).ShouldBe(OpenIddictConstants.Statuses.Valid);

        // Act
        using HttpResponseMessage response = await RefreshAsync(client, refreshToken);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("invalid_grant");
    }

    [Fact]
    public async Task RefreshTokenGrant_ShouldFail_AfterAPasswordResetWhoseRevocationDidNotRun()
    {
        // Arrange
        (RegisterRequest user, _) = await UserFactory.RegisterRandomUserWithRequestAsync(
            ApiClient, Faker, AccountConfirmationEmailSpy, CurrentPassword);

        await using WebApplicationFactory<Program> host = CreateHostThatNeverRevokes();
        using HttpClient client = host.CreateClient();

        (_, string refreshToken, _) = await SignInAsync(client, user.Email, OfflineScopes);

        string resetToken = await CreateResetTokenAsync(host.Services, user.Email);
        using HttpResponseMessage resetResponse = await client.PostAsJsonAsync(
            new Uri("auth/reset-password", UriKind.Relative),
            new ResetPasswordRequest(user.Email, resetToken, NewPassword));
        resetResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        // The row is still valid, so only the stamp check can refuse the refresh below.
        (await OpenIddictTokenState.StatusOfAsync(host.Services, refreshToken)).ShouldBe(OpenIddictConstants.Statuses.Valid);

        // Act
        using HttpResponseMessage response = await RefreshAsync(client, refreshToken);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("invalid_grant");
    }

    [Fact]
    public async Task RefreshTokenGrant_ShouldFail_AfterAPasswordResetOnThePageWhoseRevocationDidNotRun()
    {
        // Arrange
        (RegisterRequest user, _) = await UserFactory.RegisterRandomUserWithRequestAsync(
            ApiClient, Faker, AccountConfirmationEmailSpy, CurrentPassword);

        await using WebApplicationFactory<Program> host = CreateHostThatNeverRevokes();
        using HttpClient client = host.CreateClient();

        (_, string refreshToken, _) = await SignInAsync(client, user.Email, OfflineScopes);

        string resetToken = await CreateResetTokenAsync(host.Services, user.Email);
        using HttpResponseMessage resetResponse = await PostToResetPasswordPageAsync(client, new Dictionary<string, string>
        {
            ["Email"] = user.Email,
            ["Token"] = resetToken,
            ["NewPassword"] = NewPassword,
            ["ConfirmPassword"] = NewPassword
        });
        (await resetResponse.Content.ReadAsStringAsync()).ShouldContain("Hasło zmienione");

        // The row is still valid, so only the stamp check can refuse the refresh below.
        (await OpenIddictTokenState.StatusOfAsync(host.Services, refreshToken)).ShouldBe(OpenIddictConstants.Statuses.Valid);

        // Act
        using HttpResponseMessage response = await RefreshAsync(client, refreshToken);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("invalid_grant");
    }

    [Fact]
    public async Task RefreshTokenGrant_ShouldFail_WhenTheRefreshTokenCarriesNoSecurityStamp()
    {
        // Arrange: a token issued before #848 has no stamp. Letting it through would leave a way around
        // the check, so this host strips the stamp at sign-in to make such a token.
        (RegisterRequest user, _) = await UserFactory.RegisterRandomUserWithRequestAsync(
            ApiClient, Faker, AccountConfirmationEmailSpy, CurrentPassword);

        await using WebApplicationFactory<Program> host = CreateHostThatIssuesTokensWithoutAStamp();
        using HttpClient client = host.CreateClient();

        (_, string refreshToken, _) = await SignInAsync(client, user.Email, OfflineScopes);

        // The row is still valid, so only the stamp check can refuse the refresh below.
        (await OpenIddictTokenState.StatusOfAsync(host.Services, refreshToken)).ShouldBe(OpenIddictConstants.Statuses.Valid);

        // Act
        using HttpResponseMessage response = await RefreshAsync(client, refreshToken);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("invalid_grant");
    }

    [Fact]
    public async Task TokenEndpoint_ShouldKeepTheSecurityStampOutOfTheTokensAClientCanRead()
    {
        // Arrange: access tokens are not encrypted and the stamp is server-side state, so neither the
        // sign-in nor the refresh may put it into a token the client can read.
        (RegisterRequest user, _) = await UserFactory.RegisterRandomUserWithRequestAsync(
            ApiClient, Faker, AccountConfirmationEmailSpy, CurrentPassword);
        string securityStamp = await ReadSecurityStampAsync(user.Email);

        // Act
        (string accessToken, string refreshToken, string? identityToken) =
            await SignInAsync(ApiClient.Http, user.Email, "openid " + OfflineScopes);

        using HttpResponseMessage refreshResponse = await RefreshAsync(ApiClient.Http, refreshToken);
        refreshResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        string refreshedAccessToken = await ReadTokenAsync(refreshResponse, "access_token");
        string refreshedIdentityToken = await ReadTokenAsync(refreshResponse, "id_token");

        // Assert
        identityToken.ShouldNotBeNull();
        foreach (string token in new[] { accessToken, identityToken, refreshedAccessToken, refreshedIdentityToken })
        {
            JwtPayload.Read(token).ShouldNotContain(securityStamp);
        }
    }

    /// <summary>
    /// Tokens are signed and sealed with each host's own keys, so they come from the host that will check
    /// them.
    /// </summary>
    private WebApplicationFactory<Program> CreateHostThatNeverRevokes() =>
        Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                // A second relay on this database could take a row another test waits for.
                AuthSystemApiFactory.RemoveHostedService<OutboxRelay>(services);

                services.RemoveAll<IUserSessionRevoker>();
                services.AddScoped<IUserSessionRevoker, NoOpSessionRevoker>();
            }));

    private WebApplicationFactory<Program> CreateHostThatIssuesTokensWithoutAStamp() =>
        Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                AuthSystemApiFactory.RemoveHostedService<OutboxRelay>(services);

                // Runs before OpenIddict builds any token from the principal.
                services.AddOpenIddict().AddServer(options =>
                    options.AddEventHandler<OpenIddictServerEvents.ProcessSignInContext>(handler =>
                        handler
                            .UseInlineHandler(context =>
                            {
                                context.Principal?.RemoveClaims(SessionSecurityStamp.ClaimType);
                                return ValueTask.CompletedTask;
                            })
                            .SetOrder(int.MinValue)));
            }));

    private static async Task<(string AccessToken, string RefreshToken, string? IdentityToken)> SignInAsync(
        HttpClient client, string email, string scope)
    {
        using FormUrlEncodedContent loginRequest = new(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = email,
            ["password"] = CurrentPassword,
            ["client_id"] = ClientId,
            ["scope"] = scope
        });

        using HttpResponseMessage loginResponse = await client.PostAsync(
            new Uri("connect/token", UriKind.Relative), loginRequest);
        loginResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        string content = await loginResponse.Content.ReadAsStringAsync();
        using JsonDocument json = JsonDocument.Parse(content);

        return (
            json.RootElement.GetProperty("access_token").GetString()!,
            json.RootElement.GetProperty("refresh_token").GetString()!,
            json.RootElement.TryGetProperty("id_token", out JsonElement identityToken) ? identityToken.GetString() : null);
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

    private static async Task<string> ReadTokenAsync(HttpResponseMessage response, string name)
    {
        string content = await response.Content.ReadAsStringAsync();
        using JsonDocument json = JsonDocument.Parse(content);
        return json.RootElement.GetProperty(name).GetString()!;
    }

    private async Task<string> ReadSecurityStampAsync(string email)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        UserManager<ApplicationUser> userManager =
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        ApplicationUser? user = await userManager.FindByEmailAsync(email);
        user.ShouldNotBeNull();

        return await userManager.GetSecurityStampAsync(user);
    }

    /// <summary>
    /// Link tokens are sealed with data protection, and nothing makes two test hosts share a key ring, so
    /// the token comes from the host that will check it.
    /// </summary>
    private static async Task<string> CreateCancelTokenAsync(IServiceProvider services, string email)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        UserManager<ApplicationUser> userManager =
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        ApplicationUser? user = await userManager.FindByEmailAsync(email);
        user.ShouldNotBeNull();

        return await userManager.GenerateUserTokenAsync(
            user,
            AccountDeletionCancellationTokenProvider.ProviderName,
            AccountDeletionCancellationTokenProvider.CancelDeletionPurpose);
    }

    /// <inheritdoc cref="CreateCancelTokenAsync"/>
    private static async Task<string> CreateResetTokenAsync(IServiceProvider services, string email)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        UserManager<ApplicationUser> userManager =
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        ApplicationUser? user = await userManager.FindByEmailAsync(email);
        user.ShouldNotBeNull();

        return await userManager.GeneratePasswordResetTokenAsync(user);
    }

    private static async Task<HttpResponseMessage> PostToResetPasswordPageAsync(
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

        using FormUrlEncodedContent content = new(formFields);
        using HttpRequestMessage request = new(HttpMethod.Post, "/Account/ResetPassword") { Content = content };

        if (pageResponse.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? cookies))
        {
            foreach (string cookie in cookies)
            {
                request.Headers.Add("Cookie", cookie.Split(';')[0]);
            }
        }

        return await client.SendAsync(request);
    }

    [GeneratedRegex("""name="__RequestVerificationToken".*?value="([^"]+)""")]
    private static partial Regex AntiForgeryTokenRegex();

    private sealed class NoOpSessionRevoker : IUserSessionRevoker
    {
        public Task RevokeAllAsync(string userId) => Task.CompletedTask;
    }
}
