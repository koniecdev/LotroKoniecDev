using System.Net.Http.Headers;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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
    public async Task DownloadAccountData_ShouldStillHandTheExportOver_WhenADeletionIsScheduled()
    {
        // The account resource advertises the download in the deletion-scheduled branch (ADR-0052), so
        // the endpoint has to serve it there. The token was issued before the deletion and stays valid
        // for its lifetime, which is the only window this can be reached in.
        (RegisterRequest registerRequest, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, TestPassword);
        string accessToken = await GetAccessTokenAsync(registerRequest.Email, TestPassword);

        using HttpRequestMessage deleteRequest = new(HttpMethod.Post, "auth/account/delete");
        deleteRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        deleteRequest.Content = JsonContent.Create(new DeleteAccountRequest(TestPassword));
        HttpResponseMessage deleteResponse = await ApiClient.Http.SendAsync(deleteRequest);
        deleteResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        HttpResponseMessage response = await SendDownloadRequestAsync(accessToken, TestPassword);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("authData").GetProperty("deletionScheduledAt").ValueKind
            .ShouldNotBe(JsonValueKind.Null);
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

    [Fact]
    public async Task DownloadAccountData_ShouldCarryTheContactDetails_WhichTheRepresentationWithholds()
    {
        // The whole point of the step-up: the export has to hand over something the account page does
        // not already show, and the phone number is that something (#690, ADR-0052).
        (RegisterRequest registerRequest, IdentityId identityId) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, TestPassword);

        const string phoneNumber = "+48 600 100 200";
        await SetPhoneNumberAsync(identityId.Value, phoneNumber);

        string accessToken = await GetAccessTokenAsync(registerRequest.Email, TestPassword);

        using HttpRequestMessage readRequest = new(HttpMethod.Get, EndpointPath);
        readRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        HttpResponseMessage readResponse = await ApiClient.Http.SendAsync(readRequest);
        string readContent = await readResponse.Content.ReadAsStringAsync();
        using JsonDocument readJson = JsonDocument.Parse(readContent);
        readJson.RootElement.GetProperty("authData").GetProperty("phoneNumber").ValueKind
            .ShouldBe(JsonValueKind.Null);

        HttpResponseMessage downloadResponse = await SendDownloadRequestAsync(accessToken, TestPassword);
        string downloadContent = await downloadResponse.Content.ReadAsStringAsync();
        using JsonDocument downloadJson = JsonDocument.Parse(downloadContent);
        downloadJson.RootElement.GetProperty("authData").GetProperty("phoneNumber").GetString()
            .ShouldBe(phoneNumber);
    }

    private async Task SetPhoneNumberAsync(Guid userId, string phoneNumber)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        ApplicationUser user = await db.Users.FirstAsync(row => row.Id == userId);
        user.PhoneNumber = phoneNumber;
        await db.SaveChangesAsync();
    }

    private async Task<HttpResponseMessage> SendDownloadRequestAsync(string accessToken, string password)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, EndpointPath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(new DownloadAccountDataRequest(password));

        return await ApiClient.Http.SendAsync(request);
    }
}
