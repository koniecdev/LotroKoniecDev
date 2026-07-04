using Microsoft.AspNetCore.Mvc;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.EmailConfirmation;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

public sealed class ConcurrencyEndpointsTests : EndpointsTestBase
{
    public ConcurrencyEndpointsTests(AuthSystemApiFactory appFactory) : base(appFactory) { }

    [Fact]
    public async Task ConcurrentRegister_ShouldNotReturn500_WhenMultipleRequestsRegisterSameEmail()
    {
        // Arrange
        string sharedEmail = Faker.Internet.Email();
        const string sharedPassword = "TestPass1!";

        const int concurrentRequests = 10;

        RegisterRequest[] requests = Enumerable.Range(0, concurrentRequests)
            .Select(i => new RegisterRequest(
                Faker.Internet.UserName() + Faker.Random.AlphaNumeric(4) + i,
                sharedEmail,
                sharedPassword,
                AcceptedPrivacyPolicy: true,
                AcceptedDataProcessingConsent: true))
            .ToArray();

        // Act
        Task<HttpResponseMessage>[] tasks = requests
            .Select(request => ApiClient.Http.PostAsJsonAsync(
                new Uri("auth/register", UriKind.Relative), request))
            .ToArray();

        HttpResponseMessage[] responses = await Task.WhenAll(tasks);

        // Assert
        foreach (HttpResponseMessage response in responses)
        {
            if (response.StatusCode != HttpStatusCode.InternalServerError)
            {
                continue;
            }

            ProblemDetails? problemDetails = await response.Content.ReadFromJsonAsync<ProblemDetails>();
            ((int)response.StatusCode).ShouldBeLessThan(500,
                $"Internal Server Error: {problemDetails?.Detail}");
        }

        int createdCount = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
        int rejectedCount = responses.Count(r => r.StatusCode == HttpStatusCode.UnprocessableEntity);
        (createdCount + rejectedCount).ShouldBe(concurrentRequests,
            $"All responses should be either Created or UnprocessableEntity - but created: {createdCount}, rejected: {rejectedCount}");
    }

    [Fact]
    public async Task ConcurrentPasswordGrant_ShouldHandleConcurrency_WhenMultipleRequestsAuthenticateSameUser()
    {
        // Arrange
        const string password = "TestPass1!";
        (RegisterRequest request, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, password);

        const int concurrentRequests = 10;

        // Act
        Task<HttpResponseMessage>[] tasks = Enumerable.Range(0, concurrentRequests)
            .Select(async _ =>
            {
                using FormUrlEncodedContent tokenRequest = new(new Dictionary<string, string>
                {
                    ["grant_type"] = "password",
                    ["username"] = request.Username,
                    ["password"] = password,
                    ["client_id"] = "lotrokoniecdev-test",
                    ["scope"] = "email profile roles api offline_access"
                });
                return await ApiClient.Http.PostAsync(
                    new Uri("connect/token", UriKind.Relative), tokenRequest);
            })
            .ToArray();

        HttpResponseMessage[] responses = await Task.WhenAll(tasks);

        // Assert — no 500 errors, all concurrent logins should be handled gracefully
        foreach (HttpResponseMessage response in responses)
        {
            ((int)response.StatusCode).ShouldBeLessThan(500,
                $"Unexpected server error: {response.StatusCode}");
        }

        int successCount = responses.Count(r => r.StatusCode == HttpStatusCode.OK);
        successCount.ShouldBeGreaterThan(0, "At least one login should succeed");
    }

    [Fact]
    public async Task ConcurrentRefreshToken_ShouldHandleConcurrencyGracefully_WhenMultipleRequestsRefreshSameToken()
    {
        // Arrange
        const string password = "TestPass1!";
        (RegisterRequest request, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, password);

        using FormUrlEncodedContent loginRequest = new(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = request.Username,
            ["password"] = password,
            ["client_id"] = "lotrokoniecdev-test",
            ["scope"] = "email profile roles api offline_access"
        });

        HttpResponseMessage loginResponse = await ApiClient.Http.PostAsync(
            new Uri("connect/token", UriKind.Relative), loginRequest);
        loginResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        string loginContent = await loginResponse.Content.ReadAsStringAsync();
        using JsonDocument loginJson = JsonDocument.Parse(loginContent);
        string refreshToken = loginJson.RootElement.GetProperty("refresh_token").GetString()!;

        const int concurrentRequests = 10;

        // Act
        Task<HttpResponseMessage>[] tasks = Enumerable.Range(0, concurrentRequests)
            .Select(async _ =>
            {
                using FormUrlEncodedContent refreshRequest = new(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = refreshToken,
                    ["client_id"] = "lotrokoniecdev-test"
                });
                return await ApiClient.Http.PostAsync(
                    new Uri("connect/token", UriKind.Relative), refreshRequest);
            })
            .ToArray();

        HttpResponseMessage[] responses = await Task.WhenAll(tasks);

        // Assert
        foreach (HttpResponseMessage response in responses)
        {
            ((int)response.StatusCode).ShouldBeLessThan(500,
                $"Unexpected server error: {response.StatusCode}");
        }

        int successCount = responses.Count(r => r.StatusCode == HttpStatusCode.OK);
        successCount.ShouldBeGreaterThanOrEqualTo(1, "At least one refresh should succeed");
    }

    [Fact]
    public async Task ConcurrentConfirmEmail_ShouldNotReturn500_WhenMultipleRequestsConfirmSameEmail()
    {
        // Arrange
        (RegisterRequest _, _) =
            await UserFactory.RegisterRandomUserUnconfirmedAsync(ApiClient, Faker, AccountConfirmationEmailSpy);

        string email = AccountConfirmationEmailSpy.LastEmail!;
        string token = AccountConfirmationEmailSpy.LastConfirmationToken!;

        const int concurrentRequests = 10;

        // Act
        Task<HttpResponseMessage>[] tasks = Enumerable.Range(0, concurrentRequests)
            .Select(_ => ApiClient.Http.PostAsJsonAsync(
                new Uri("auth/confirm-email", UriKind.Relative),
                new ConfirmEmailRequest(email, token)))
            .ToArray();

        HttpResponseMessage[] responses = await Task.WhenAll(tasks);

        // Assert — no 500 errors
        foreach (HttpResponseMessage response in responses)
        {
            ((int)response.StatusCode).ShouldBeLessThan(500,
                $"Unexpected server error: {response.StatusCode}");
        }

        int okCount = responses.Count(r => r.StatusCode == HttpStatusCode.OK);
        int badRequestCount = responses.Count(r => r.StatusCode == HttpStatusCode.BadRequest);
        (okCount + badRequestCount).ShouldBe(concurrentRequests,
            $"All responses should be either OK or BadRequest — ok: {okCount}, badRequest: {badRequestCount}");
        okCount.ShouldBeGreaterThanOrEqualTo(1, "At least one confirmation should succeed");
    }
}
