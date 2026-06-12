using System.Net.Http.Headers;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;

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

        string accessToken = await GetAccessTokenAsync(registerRequest.Username, password);

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
        authData.GetProperty("dataProcessingConsentGiven").GetBoolean().ShouldBeTrue();
        authData.GetProperty("privacyPolicyAccepted").GetBoolean().ShouldBeTrue();
        authData.GetProperty("emailConfirmed").GetBoolean().ShouldBeTrue();
        authData.GetProperty("roles").GetArrayLength().ShouldBeGreaterThan(0);

        json.RootElement.GetProperty("isComplete").GetBoolean().ShouldBeTrue();
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

}
