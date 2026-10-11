using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

public sealed class ExportAccountDataEndpointTests : EndpointsTestBase
{
    public ExportAccountDataEndpointTests(AuthSystemApiFactory appFactory) : base(appFactory) { }

    [Fact]
    public async Task ExportAccountData_ShouldReturnOk_WhenAuthenticated()
    {
        // Arrange
        const string password = "TestPass1!";

        (RegisterRequest registerRequest, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, password);

        string accessToken = await GetAccessTokenAsync(registerRequest.Email, password);

        using HttpRequestMessage request = new(HttpMethod.Get, "auth/account/data-export");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        // Act
        HttpResponseMessage response = await ApiClient.Http.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        string content = await response.Content.ReadAsStringAsync();
        using JsonDocument json = JsonDocument.Parse(content);

        JsonElement authData = json.RootElement.GetProperty("authData");
        authData.GetProperty("username").GetString().ShouldBe(registerRequest.Username);
        authData.GetProperty("email").GetString().ShouldBe(registerRequest.Email);
        // Registration asks for no data-processing consent since #1073; the field stays for older accounts.
        authData.GetProperty("dataProcessingConsentGiven").GetBoolean().ShouldBeFalse();
        authData.GetProperty("dataProcessingConsentDate").ValueKind.ShouldBe(JsonValueKind.Null);
        authData.GetProperty("privacyPolicyAccepted").GetBoolean().ShouldBeTrue();
        authData.GetProperty("termsOfServiceAccepted").GetBoolean().ShouldBeTrue();
        authData.GetProperty("termsOfServiceAcceptedDate").ValueKind.ShouldNotBe(JsonValueKind.Null);
        authData.GetProperty("emailConfirmed").GetBoolean().ShouldBeTrue();
        authData.GetProperty("roles").GetArrayLength().ShouldBeGreaterThan(0);

        json.RootElement.GetProperty("isComplete").GetBoolean().ShouldBeTrue();
    }

    /// <summary>
    /// Accounts made before #1073 still hold the consent they gave, and the export is their copy of
    /// everything we store, so it has to carry the stored value, not the default.
    /// </summary>
    [Fact]
    public async Task ExportAccountData_ShouldCarryTheStoredConsent_WhenAnOlderAccountGaveIt()
    {
        // Arrange
        const string password = "TestPass1!";

        (RegisterRequest registerRequest, IdentityId identityId) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, password);
        await GiveTheDataProcessingConsentAsAnOlderAccountDidAsync(identityId.Value);

        string accessToken = await GetAccessTokenAsync(registerRequest.Email, password);

        using HttpRequestMessage request = new(HttpMethod.Get, "auth/account/data-export");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        // Act
        HttpResponseMessage response = await ApiClient.Http.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement authData = json.RootElement.GetProperty("authData");
        authData.GetProperty("dataProcessingConsentGiven").GetBoolean().ShouldBeTrue();
        authData.GetProperty("dataProcessingConsentDate").ValueKind.ShouldBe(JsonValueKind.String);
    }

    [Fact]
    public async Task ExportAccountData_ShouldReturnUnauthorized_WhenNotAuthenticated()
    {
        // Act
        HttpResponseMessage response = await ApiClient.Http.GetAsync(
            new Uri("auth/account/data-export", UriKind.Relative));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private async Task GiveTheDataProcessingConsentAsAnOlderAccountDidAsync(Guid userId)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        DateTimeOffset givenAt = DateTimeOffset.UtcNow;
        await db.Users
            .Where(user => user.Id == userId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(user => user.DataProcessingConsentGiven, true)
                .SetProperty(user => user.DataProcessingConsentDate, givenAt));
    }
}
