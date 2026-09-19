using System.Net.Http.Headers;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

/// <summary>
/// The GDPR export behind the current password (#690, ADR-0052). A token alone opens the account
/// representation, which the account page renders, but it must never hand the export over.
/// </summary>
public sealed class DownloadAccountDataEndpointTests : EndpointsTestBase
{
    private const string TestPassword = "TestPass1!";
    private const string EndpointPath = "auth/account/data-export";

    public DownloadAccountDataEndpointTests(AuthSystemApiFactory appFactory) : base(appFactory) { }

    [Fact]
    public async Task DownloadAccountData_ShouldReturnTheExport_WhenThePasswordIsCorrect()
    {
        (RegisterRequest registerRequest, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, TestPassword);

        string accessToken = await GetAccessTokenAsync(registerRequest.Email, TestPassword);

        HttpResponseMessage response = await SendDownloadRequestAsync(accessToken, TestPassword);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        string content = await response.Content.ReadAsStringAsync();
        using JsonDocument json = JsonDocument.Parse(content);

        JsonElement authData = json.RootElement.GetProperty("authData");
        authData.GetProperty("username").GetString().ShouldBe(registerRequest.Username);
        authData.GetProperty("email").GetString().ShouldBe(registerRequest.Email);
        json.RootElement.GetProperty("isComplete").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task DownloadAccountData_ShouldRefuseAndServeNoData_WhenThePasswordIsWrong()
    {
        (RegisterRequest registerRequest, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, TestPassword);

        string accessToken = await GetAccessTokenAsync(registerRequest.Email, TestPassword);

        HttpResponseMessage response = await SendDownloadRequestAsync(accessToken, "Wrong-Password1!");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        string content = await response.Content.ReadAsStringAsync();
        content.ShouldNotContain(registerRequest.Email);
        content.ShouldNotContain(registerRequest.Username);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task DownloadAccountData_ShouldRefuse_WhenNoPasswordIsSent(string password)
    {
        (RegisterRequest registerRequest, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, TestPassword);

        string accessToken = await GetAccessTokenAsync(registerRequest.Email, TestPassword);

        HttpResponseMessage response = await SendDownloadRequestAsync(accessToken, password);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        string content = await response.Content.ReadAsStringAsync();
        content.ShouldNotContain(registerRequest.Email);
    }

    [Fact]
    public async Task DownloadAccountData_ShouldRefuse_WhenTheCallerSendsAnotherAccountsPassword()
    {
        // The password has to belong to the account the token names, not merely be a valid password.
        (RegisterRequest victim, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, TestPassword);
        const string otherPassword = "OtherPass1!";
        await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, otherPassword);

        string accessToken = await GetAccessTokenAsync(victim.Email, TestPassword);

        HttpResponseMessage response = await SendDownloadRequestAsync(accessToken, otherPassword);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task DownloadAccountData_ShouldReturnUnauthorized_WhenNotAuthenticated()
    {
        using HttpRequestMessage request = new(HttpMethod.Post, EndpointPath)
        {
            Content = JsonContent.Create(new DownloadAccountDataRequest(TestPassword))
        };

        HttpResponseMessage response = await ApiClient.Http.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private async Task<HttpResponseMessage> SendDownloadRequestAsync(string accessToken, string password)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, EndpointPath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(new DownloadAccountDataRequest(password));

        return await ApiClient.Http.SendAsync(request);
    }
}
