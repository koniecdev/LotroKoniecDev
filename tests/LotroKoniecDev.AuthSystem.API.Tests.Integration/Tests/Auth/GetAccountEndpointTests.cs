using System.Net.Http.Headers;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

public sealed class GetAccountEndpointTests : EndpointsTestBase
{
    private const string AccountPath = "auth/account";
    private const string Password = "TestPass1!";

    public GetAccountEndpointTests(AuthSystemApiFactory appFactory) : base(appFactory) { }

    [Fact]
    public async Task GetAccount_ShouldReturnWhatTheAccountPageShows_WhenAuthenticated()
    {
        // Arrange
        (RegisterRequest registerRequest, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, Password);

        string accessToken = await GetAccessTokenAsync(registerRequest.Email, Password);

        using HttpRequestMessage request = new(HttpMethod.Get, AccountPath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        // Act
        HttpResponseMessage response = await ApiClient.Http.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        string content = await response.Content.ReadAsStringAsync();
        using JsonDocument json = JsonDocument.Parse(content);

        JsonElement account = json.RootElement.GetProperty("account");
        account.GetProperty("username").GetString().ShouldBe(registerRequest.Username);
        account.GetProperty("email").GetString().ShouldBe(registerRequest.Email);
        account.GetProperty("emailConfirmed").GetBoolean().ShouldBeTrue();
        account.GetProperty("roles").GetArrayLength().ShouldBeGreaterThan(0);
        account.GetProperty("privacyPolicyAccepted").GetBoolean().ShouldBeTrue();
        account.GetProperty("deletionScheduledAt").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task GetAccount_ShouldLeaveOutWhatOnlyTheExportCarries_WhenAuthenticated()
    {
        // Arrange - the page shows neither the user id nor the phone number. They stay behind the
        // password, in the export (#690).
        (RegisterRequest registerRequest, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, Password);

        string accessToken = await GetAccessTokenAsync(registerRequest.Email, Password);

        using HttpRequestMessage request = new(HttpMethod.Get, AccountPath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        // Act
        HttpResponseMessage response = await ApiClient.Http.SendAsync(request);

        // Assert
        string content = await response.Content.ReadAsStringAsync();
        using JsonDocument json = JsonDocument.Parse(content);

        JsonElement account = json.RootElement.GetProperty("account");
        account.TryGetProperty("userId", out _).ShouldBeFalse();
        account.TryGetProperty("phoneNumber", out _).ShouldBeFalse();
        json.RootElement.TryGetProperty("authData", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task GetAccount_ShouldReturnUnauthorized_WhenNotAuthenticated()
    {
        // Act
        HttpResponseMessage response = await ApiClient.Http.GetAsync(new Uri(AccountPath, UriKind.Relative));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
