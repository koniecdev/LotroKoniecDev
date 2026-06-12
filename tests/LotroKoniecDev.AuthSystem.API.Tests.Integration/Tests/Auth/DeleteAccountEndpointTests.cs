using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.SharedKernel.Constants;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

public sealed class DeleteAccountEndpointTests : EndpointsTestBase
{
    public DeleteAccountEndpointTests(AuthSystemApiFactory appFactory) : base(appFactory) { }

    [Fact]
    public async Task DeleteAccount_ShouldReturnNoContent_WhenPasswordIsCorrect()
    {
        // Arrange
        const string password = "TestPass1!";

        (RegisterRequest registerRequest, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, password);

        string accessToken = await GetAccessTokenAsync(registerRequest.Username, password);

        DeleteAccountRequest deleteRequest = new(password);

        using HttpRequestMessage request = new(HttpMethod.Post, "auth/account/delete");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(deleteRequest);

        // Act
        HttpResponseMessage response = await ApiClient.Http.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task DeleteAccount_ShouldPreventLogin_AfterDeletion()
    {
        // Arrange
        const string password = "TestPass1!";

        (RegisterRequest registerRequest, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, password);

        string accessToken = await GetAccessTokenAsync(registerRequest.Username, password);

        DeleteAccountRequest deleteRequest = new(password);

        using HttpRequestMessage deleteReq = new(HttpMethod.Post, "auth/account/delete");
        deleteReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        deleteReq.Content = JsonContent.Create(deleteRequest);

        await ApiClient.Http.SendAsync(deleteReq);

        // Act — try to login with old credentials
        using FormUrlEncodedContent tokenRequest = new(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = registerRequest.Username,
            ["password"] = password,
            ["client_id"] = "lotrokoniecdev-test",
            ["scope"] = "email profile roles api"
        });

        HttpResponseMessage loginResponse = await ApiClient.Http.PostAsync(
            new Uri("connect/token", UriKind.Relative), tokenRequest);

        // Assert — login should fail (user anonymized + locked)
        loginResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task DeleteAccount_ShouldReturnBadRequest_WhenPasswordIsWrong()
    {
        // Arrange
        const string password = "TestPass1!";

        (RegisterRequest registerRequest, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, password);

        string accessToken = await GetAccessTokenAsync(registerRequest.Username, password);

        DeleteAccountRequest deleteRequest = new("WrongPassword1!");

        using HttpRequestMessage request = new(HttpMethod.Post, "auth/account/delete");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(deleteRequest);

        // Act
        HttpResponseMessage response = await ApiClient.Http.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task DeleteAccount_ShouldReturnUnauthorized_WhenNotAuthenticated()
    {
        // Arrange
        DeleteAccountRequest deleteRequest = new("TestPass1!");

        // Act
        using HttpRequestMessage request = new(HttpMethod.Post, "auth/account/delete");
        request.Content = JsonContent.Create(deleteRequest);
        HttpResponseMessage response = await ApiClient.Http.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task DeleteAccount_ShouldAnonymizeUserData_WhenAccountIsDeleted()
    {
        // Arrange
        const string password = "TestPass1!";

        (RegisterRequest registerRequest, IdentityId identityId) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, password);

        string accessToken = await GetAccessTokenAsync(registerRequest.Username, password);

        DeleteAccountRequest deleteRequest = new(password);

        using HttpRequestMessage request = new(HttpMethod.Post, "auth/account/delete");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(deleteRequest);

        // Act
        HttpResponseMessage response = await ApiClient.Http.SendAsync(request);

        // Assert — deletion succeeded
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Assert — verify anonymization in database
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        ApplicationUser user = await db.Users.FirstAsync(u => u.Id == identityId.Value);

        // PII fields should be anonymized
        user.UserName.ShouldNotBe(registerRequest.Username);
        user.Email.ShouldEndWith(AnonymizationConstants.EmailDomain);
        user.Email.ShouldStartWith(AnonymizationConstants.EmailPrefix);
        user.PhoneNumber.ShouldBeNull();
        user.PasswordHash.ShouldBeNull();

        // Consent flags should be cleared
        user.DataProcessingConsentGiven.ShouldBeFalse();
        user.DataProcessingConsentDate.ShouldBeNull();
        user.PrivacyPolicyAccepted.ShouldBeFalse();
        user.PrivacyPolicyAcceptedDate.ShouldBeNull();

        // Security fields should be reset
        user.EmailConfirmed.ShouldBeFalse();
        user.PhoneNumberConfirmed.ShouldBeFalse();
        user.TwoFactorEnabled.ShouldBeFalse();
        user.AccessFailedCount.ShouldBe(0);

        // Account should be permanently locked
        user.LockoutEnabled.ShouldBeTrue();
        user.LockoutEnd.ShouldBe(DateTimeOffset.MaxValue);

        // Roles should be removed
        bool hasRoles = await db.UserRoles.AnyAsync(ur => ur.UserId == identityId.Value);
        hasRoles.ShouldBeFalse();

        // Claims should be removed
        bool hasClaims = await db.UserClaims.AnyAsync(uc => uc.UserId == identityId.Value);
        hasClaims.ShouldBeFalse();
    }

}
