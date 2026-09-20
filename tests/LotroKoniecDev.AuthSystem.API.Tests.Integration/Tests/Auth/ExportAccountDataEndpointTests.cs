using System.Net.Http.Headers;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

/// <summary>
/// The export hands over a whole account in one file, so a valid token alone must not be enough
/// (#690). Every test here holds a good token; only the password decides.
/// </summary>
public sealed class ExportAccountDataEndpointTests : EndpointsTestBase
{
    private const string DataExportPath = "auth/account/data-export";
    private const string Password = "TestPass1!";

    public ExportAccountDataEndpointTests(AuthSystemApiFactory appFactory) : base(appFactory) { }

    [Fact]
    public async Task ExportAccountData_ShouldReturnTheExport_WhenThePasswordIsCorrect()
    {
        // Arrange
        (RegisterRequest registerRequest, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, Password);

        string accessToken = await GetAccessTokenAsync(registerRequest.Email, Password);

        using HttpRequestMessage request = CreateExportRequest(accessToken, new ExportAccountDataRequest(Password));

        // Act
        HttpResponseMessage response = await ApiClient.Http.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl?.NoStore.ShouldBe(true);

        string content = await response.Content.ReadAsStringAsync();
        using JsonDocument json = JsonDocument.Parse(content);

        JsonElement authData = json.RootElement.GetProperty("authData");
        authData.GetProperty("username").GetString().ShouldBe(registerRequest.Username);
        authData.GetProperty("email").GetString().ShouldBe(registerRequest.Email);
        authData.GetProperty("dataProcessingConsentGiven").GetBoolean().ShouldBeTrue();
        authData.GetProperty("privacyPolicyAccepted").GetBoolean().ShouldBeTrue();
        authData.GetProperty("termsOfServiceAccepted").GetBoolean().ShouldBeTrue();
        authData.GetProperty("termsOfServiceAcceptedDate").ValueKind.ShouldNotBe(JsonValueKind.Null);
        authData.GetProperty("emailConfirmed").GetBoolean().ShouldBeTrue();
        authData.GetProperty("roles").GetArrayLength().ShouldBeGreaterThan(0);

        json.RootElement.GetProperty("isComplete").GetBoolean().ShouldBeTrue();
    }

    [Theory]
    [InlineData("WrongPass1!", "Auth.InvalidCurrentPassword")]
    [InlineData("testpass1!", "Auth.InvalidCurrentPassword")]
    [InlineData("", "Auth.ExportPasswordRequired")]
    [InlineData("   ", "Auth.ExportPasswordRequired")]
    public async Task ExportAccountData_ShouldRefuseAndReturnNoData_WhenThePasswordIsWrongOrMissing(
        string password, string expectedErrorCode)
    {
        // Arrange
        (RegisterRequest registerRequest, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, Password);

        string accessToken = await GetAccessTokenAsync(registerRequest.Email, Password);

        using HttpRequestMessage request = CreateExportRequest(accessToken, new ExportAccountDataRequest(password));

        // Act
        HttpResponseMessage response = await ApiClient.Http.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        string content = await response.Content.ReadAsStringAsync();
        content.ShouldContain(expectedErrorCode);
        content.ShouldNotContain(registerRequest.Email);
        content.ShouldNotContain("authData");
    }

    [Fact]
    public async Task ExportAccountData_ShouldRefuse_WhenTheBodyCarriesNoPasswordField()
    {
        // Arrange
        (RegisterRequest registerRequest, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, Password);

        string accessToken = await GetAccessTokenAsync(registerRequest.Email, Password);

        using HttpRequestMessage request = CreateExportRequest(accessToken, new { });

        // Act
        HttpResponseMessage response = await ApiClient.Http.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldNotContain("authData");
    }

    [Fact]
    public async Task ExportAccountData_ShouldRefuseWithThePasswordError_WhenThereIsNoBodyAtAll()
    {
        // Arrange - the refusal has to come from the handler, because that is where the audit line
        // is written.
        (RegisterRequest registerRequest, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, Password);

        string accessToken = await GetAccessTokenAsync(registerRequest.Email, Password);

        using HttpRequestMessage request = new(HttpMethod.Post, DataExportPath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        // Act
        HttpResponseMessage response = await ApiClient.Http.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("Auth.ExportPasswordRequired");
    }

    [Fact]
    public async Task ExportAccountData_ShouldNotAnswerAGet_WhenTheCallerHoldsAValidToken()
    {
        // Arrange - the old export was a GET that asked for nothing. It must not come back.
        (RegisterRequest registerRequest, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, Password);

        string accessToken = await GetAccessTokenAsync(registerRequest.Email, Password);

        using HttpRequestMessage request = new(HttpMethod.Get, DataExportPath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        // Act
        HttpResponseMessage response = await ApiClient.Http.SendAsync(request);

        // Assert
        response.IsSuccessStatusCode.ShouldBeFalse();
        (await response.Content.ReadAsStringAsync()).ShouldNotContain("authData");
    }

    [Fact]
    public async Task ExportAccountData_ShouldReturnUnauthorized_WhenNotAuthenticated()
    {
        // Arrange
        using HttpRequestMessage request = new(HttpMethod.Post, DataExportPath);
        request.Content = JsonContent.Create(new ExportAccountDataRequest(Password));

        // Act
        HttpResponseMessage response = await ApiClient.Http.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private static HttpRequestMessage CreateExportRequest(string accessToken, object body)
    {
        HttpRequestMessage request = new(HttpMethod.Post, DataExportPath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(body);
        return request;
    }
}
