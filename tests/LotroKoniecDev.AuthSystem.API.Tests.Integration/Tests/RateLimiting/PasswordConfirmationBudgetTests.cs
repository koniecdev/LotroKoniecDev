using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Password;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.RateLimiting;

/// <summary>
/// The budget of current-password confirmations that belongs to the account (#813, ADR-0053). Every
/// call to these endpoints arrives from the frontend, so an IP policy is one bucket for every user: it
/// cannot stop a session thief from guessing, and one user's traffic can refuse another's action.
/// The first three tests run on the suite's normal Testing host, where the limiter middleware is off, so
/// the only thing that can refuse a request there is this budget. The last one turns the middleware on
/// for a derived host, to prove the endpoints are off the shared bucket.
/// </summary>
public sealed class PasswordConfirmationBudgetTests : EndpointsTestBase
{
    /// <summary>Mirrors PasswordConfirmationThrottle: 10 confirmations per 15 minutes per account.</summary>
    private const int PermitLimit = 10;

    /// <summary>Mirrors the auth-endpoint-limit policy: 10 requests per minute per address.</summary>
    private const int SharedIpBucket = 10;

    private const string Password = "TestPass1!";
    private const string WrongPassword = "Wrong-Password1!";
    private const string NewPassword = "NewPass99!";
    private const string ThrottledErrorCode = "Auth.PasswordConfirmationThrottled";

    private const string DeletePath = "auth/account/delete";
    private const string ChangePasswordPath = "auth/change-password";
    private const string ChangeEmailPath = "auth/account/change-email";
    private const string DataExportPath = "auth/account/data-export";

    public PasswordConfirmationBudgetTests(AuthSystemApiFactory appFactory) : base(appFactory)
    {
    }

    [Fact]
    public async Task DeleteAccount_ShouldRefuseEvenTheRightPassword_OnceTheBudgetIsSpent()
    {
        // Arrange
        (RegisterRequest registerRequest, IdentityId identityId) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, Password);
        string accessToken = await GetAccessTokenAsync(registerRequest.Email, Password);

        // Act: spend the whole budget on wrong guesses, then send the right password
        HttpStatusCode[] guesses = new HttpStatusCode[PermitLimit];
        for (int i = 0; i < guesses.Length; i++)
        {
            using HttpResponseMessage guess = await PostAsync(
                ApiClient.Http, DeletePath, accessToken, new DeleteAccountRequest(WrongPassword));
            guesses[i] = guess.StatusCode;
        }

        using HttpResponseMessage refused = await PostAsync(
            ApiClient.Http, DeletePath, accessToken, new DeleteAccountRequest(Password));

        // Assert: the brake belongs to the account, so the address the guesses came from never mattered
        guesses.ShouldAllBe(statusCode => statusCode == HttpStatusCode.BadRequest);
        refused.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await ReadErrorCodeAsync(refused)).ShouldBe(ThrottledErrorCode);

        // Nothing was scheduled: the right password was never checked once the budget was gone
        ApplicationUser user = await LoadUserAsync(identityId.Value);
        user.DeletionScheduledAt.ShouldBeNull();
    }

    [Fact]
    public async Task PasswordConfirmation_ShouldShareOneBudgetAcrossTheFourEndpoints()
    {
        // Arrange: four endpoints ask for the current password, and a guesser picks whichever is open.
        // One budget across all four is what makes it a brake on guessing rather than on one form.
        (RegisterRequest registerRequest, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, Password);
        string accessToken = await GetAccessTokenAsync(registerRequest.Email, Password);

        // Act: spread the budget over the four endpoints — 3 + 3 + 2 + 2 = 10 wrong guesses
        List<HttpStatusCode> guesses = [];
        for (int i = 0; i < 3; i++)
        {
            guesses.Add(await PostForStatusAsync(DeletePath, accessToken, new DeleteAccountRequest(WrongPassword)));
            guesses.Add(await PostForStatusAsync(ChangePasswordPath, accessToken, new ChangePasswordRequest(WrongPassword, NewPassword)));
        }

        for (int i = 0; i < 2; i++)
        {
            guesses.Add(await PostForStatusAsync(ChangeEmailPath, accessToken, new ChangeEmailRequest(Faker.Internet.Email(), WrongPassword)));
            guesses.Add(await PostForStatusAsync(DataExportPath, accessToken, new DownloadAccountDataRequest(WrongPassword)));
        }

        using HttpResponseMessage refused = await PostAsync(
            ApiClient.Http, ChangePasswordPath, accessToken, new ChangePasswordRequest(Password, NewPassword));

        // Assert
        guesses.Count.ShouldBe(PermitLimit);
        guesses.ShouldAllBe(statusCode => statusCode == HttpStatusCode.BadRequest);
        refused.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await ReadErrorCodeAsync(refused)).ShouldBe(ThrottledErrorCode);

        // The old password still logs in: the refusal changed nothing on the account
        string tokenAfterRefusal = await GetAccessTokenAsync(registerRequest.Email, Password);
        tokenAfterRefusal.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task PasswordConfirmation_ShouldKeepOneAccountBudgetOutOfAnother_WhateverTheAddress()
    {
        // Arrange: both users' requests arrive from the same client, which is exactly how the auth API
        // sees every user behind the frontend
        (RegisterRequest attacked, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, Password);
        (RegisterRequest bystander, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, Password);
        string attackedToken = await GetAccessTokenAsync(attacked.Email, Password);
        string bystanderToken = await GetAccessTokenAsync(bystander.Email, Password);

        for (int i = 0; i < PermitLimit + 1; i++)
        {
            using HttpResponseMessage guess = await PostAsync(
                ApiClient.Http, DeletePath, attackedToken, new DeleteAccountRequest(WrongPassword));
        }

        // Act
        using HttpResponseMessage bystanderChange = await PostAsync(
            ApiClient.Http, ChangePasswordPath, bystanderToken, new ChangePasswordRequest(Password, NewPassword));

        // Assert: one account's spent budget must not make another account's password change fail
        bystanderChange.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ChangePassword_ShouldNotBeRefusedByTheSharedIpBucket_WhenAnotherUserSpentIt()
    {
        // Arrange: the limiter is on for this host, and every request here carries the same address —
        // the frontend's situation. One user spends the auth-endpoint-limit bucket on account page
        // views; another user's password change must not pay for it (ADR-0053).
        (RegisterRequest heavyUser, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, Password);
        (RegisterRequest bystander, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, Password);

        using WebApplicationFactory<Program> limitedHost = CreateRateLimitedHost();
        using HttpClient limitedClient = limitedHost.CreateClient();

        // The token endpoint sits on the same bucket, so the two tokens are the first two permits
        string heavyToken = await GetAccessTokenAsync(limitedClient, heavyUser.Email);
        string bystanderToken = await GetAccessTokenAsync(limitedClient, bystander.Email);

        int pageViews = SharedIpBucket - 2;
        for (int i = 0; i < pageViews; i++)
        {
            using HttpResponseMessage pageView = await GetAsync(limitedClient, DataExportPath, heavyToken);
            pageView.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using HttpResponseMessage bucketSpent = await GetAsync(limitedClient, DataExportPath, heavyToken);
        bucketSpent.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        // Act
        using HttpResponseMessage bystanderChange = await PostAsync(
            limitedClient, ChangePasswordPath, bystanderToken, new ChangePasswordRequest(Password, NewPassword));

        // Assert
        bystanderChange.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(DeletePath)]
    [InlineData(ChangePasswordPath)]
    [InlineData(DataExportPath)]
    public void PasswordConfirmationEndpoint_ShouldCarryNoRateLimitPolicy(string route)
    {
        // The per-account budget is the brake (ADR-0053). Putting one of these back on a per-address
        // policy would silently restore the shared bucket, so the metadata is pinned here.
        RouteEndpoint endpoint = FindPostEndpoint(route);

        endpoint.Metadata.GetMetadata<DisableRateLimitingAttribute>().ShouldNotBeNull();
    }

    [Fact]
    public void RequestEmailChange_ShouldKeepTheMailBudgetPolicy()
    {
        // change-email-limit bounds the mail the endpoint sends, which the confirmation budget does not
        // replace; the endpoint carries both.
        RouteEndpoint endpoint = FindPostEndpoint(ChangeEmailPath);

        endpoint.Metadata.GetMetadata<DisableRateLimitingAttribute>().ShouldBeNull();
        EnableRateLimitingAttribute? policy = endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>();
        policy.ShouldNotBeNull();
        policy.PolicyName.ShouldBe("change-email-limit");
    }

    private RouteEndpoint FindPostEndpoint(string route)
    {
        // The data-export route serves a GET too, so the verb is part of the match.
        return Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Single(endpoint =>
                endpoint.RoutePattern.RawText?.TrimStart('/') == route
                && endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(HttpMethods.Post) is true);
    }

    private async Task<HttpStatusCode> PostForStatusAsync(string path, string accessToken, object body)
    {
        using HttpResponseMessage response = await PostAsync(ApiClient.Http, path, accessToken, body);
        return response.StatusCode;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient http, string path, string accessToken, object body)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(body, body.GetType());
        return await http.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient http, string path, string accessToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await http.SendAsync(request);
    }

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        string content = await response.Content.ReadAsStringAsync();
        using JsonDocument json = JsonDocument.Parse(content);
        return json.RootElement.TryGetProperty("errorCode", out JsonElement errorCode)
            ? errorCode.GetString()
            : null;
    }

    private static async Task<string> GetAccessTokenAsync(HttpClient http, string email)
    {
        using FormUrlEncodedContent tokenRequest = new(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = email,
            ["password"] = Password,
            ["client_id"] = "lotrokoniecdev-test",
            ["scope"] = "email profile roles api"
        });

        using HttpResponseMessage tokenResponse = await http.PostAsync(
            new Uri("connect/token", UriKind.Relative), tokenRequest);
        tokenResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        using JsonDocument json = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("access_token").GetString()!;
    }

    private async Task<ApplicationUser> LoadUserAsync(Guid userId)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        return await db.Users.AsNoTracking().FirstAsync(row => row.Id == userId);
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
