using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
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

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

public sealed class CancelAccountDeletionEndpointTests : EndpointsTestBase
{
    private const string TestPassword = "TestPass1!";

    public CancelAccountDeletionEndpointTests(AuthSystemApiFactory appFactory) : base(appFactory) { }

    [Fact]
    public async Task CancelDeletion_ShouldClearScheduleAndLockout_WhenTokenIsValid()
    {
        // Arrange
        (RegisterRequest registerRequest, IdentityId identityId) = await RegisterAndScheduleDeletionAsync();
        string cancelToken = AccountDeletionEmailSpy.LastCancelToken!;

        // Act
        HttpResponseMessage response = await SendCancelRequestAsync(registerRequest.Email, cancelToken);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        ApplicationUser user = await GetUserAsync(identityId.Value);
        user.DeletionScheduledAt.ShouldBeNull();
        user.LockoutEnd.ShouldBeNull();
    }

    [Fact]
    public async Task CancelDeletion_ShouldInvalidateOldPassword_AndForcePasswordReset()
    {
        // Arrange
        (RegisterRequest registerRequest, IdentityId identityId) = await RegisterAndScheduleDeletionAsync();
        string cancelToken = AccountDeletionEmailSpy.LastCancelToken!;

        // Act
        HttpResponseMessage response = await SendCancelRequestAsync(registerRequest.Email, cancelToken);

        // Assert — the possibly-compromised password dies with the cancellation
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        ApplicationUser user = await GetUserAsync(identityId.Value);
        user.PasswordHash.ShouldBeNull();

        HttpResponseMessage oldPasswordLogin = await RequestTokenAsync(registerRequest.Email, TestPassword);
        oldPasswordLogin.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CancelDeletion_ShouldReturnResetToken_ThatCompletesTheRecoveryFlow()
    {
        // Arrange
        (RegisterRequest registerRequest, _) = await RegisterAndScheduleDeletionAsync();
        string cancelToken = AccountDeletionEmailSpy.LastCancelToken!;

        // Act — cancel, then walk the forced reset flow end to end
        HttpResponseMessage cancelResponse = await SendCancelRequestAsync(registerRequest.Email, cancelToken);
        cancelResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        CancelAccountDeletionResponse cancelBody = (await cancelResponse.Content
            .ReadFromJsonAsync<CancelAccountDeletionResponse>(ApiClient.JsonOptions))!;
        cancelBody.PasswordResetToken.ShouldNotBeNullOrWhiteSpace();

        const string newPassword = "BrandNewPass1!";
        ResetPasswordRequest resetRequest = new(registerRequest.Email, cancelBody.PasswordResetToken, newPassword);
        HttpResponseMessage resetResponse = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/reset-password", UriKind.Relative), resetRequest);

        // Assert — account fully recovered with the new password
        resetResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        HttpResponseMessage newPasswordLogin = await RequestTokenAsync(registerRequest.Email, newPassword);
        newPasswordLogin.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task CancelDeletion_ShouldDeliverConfirmationEmail()
    {
        // Arrange
        (RegisterRequest registerRequest, _) = await RegisterAndScheduleDeletionAsync();
        string cancelToken = AccountDeletionEmailSpy.LastCancelToken!;

        // Act — the courtesy notice arrives through the pipeline, so the capture has to be
        // awaited (ADR-0038)
        HttpResponseMessage response = await SendCancelRequestAsync(registerRequest.Email, cancelToken);
        await AccountDeletionEmailSpy.WaitForCancelledCaptureAsync();

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        AccountDeletionEmailSpy.CancelledCallCount.ShouldBe(1);
        AccountDeletionEmailSpy.LastCancelledEmail.ShouldBe(registerRequest.Email);
    }

    [Fact]
    public async Task CancelDeletion_ShouldReturnBadRequest_WhenTokenIsGarbage()
    {
        // Arrange
        (RegisterRequest registerRequest, IdentityId identityId) = await RegisterAndScheduleDeletionAsync();

        // Act
        HttpResponseMessage response = await SendCancelRequestAsync(registerRequest.Email, "not-a-real-token");

        // Assert — schedule stays in place
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("Auth.InvalidCancelDeletionToken");

        ApplicationUser user = await GetUserAsync(identityId.Value);
        user.DeletionScheduledAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task CancelDeletion_ShouldReturnBadRequest_WhenEmailIsUnknown()
    {
        // Act
        HttpResponseMessage response = await SendCancelRequestAsync(
            "nobody@example.com", "some-token");

        // Assert — same generic error as a bad token (no account-state probing)
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("Auth.InvalidCancelDeletionToken");
    }

    [Fact]
    public async Task CancelDeletion_ShouldReturnBadRequest_WhenDeletionIsNotScheduled()
    {
        // Arrange — registered user without a scheduled deletion
        (RegisterRequest registerRequest, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, TestPassword);

        // Act
        HttpResponseMessage response = await SendCancelRequestAsync(registerRequest.Email, "some-token");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("Auth.InvalidCancelDeletionToken");
    }

    [Fact]
    public async Task CancelDeletion_ShouldRejectTokenReplay_AfterSuccessfulCancellation()
    {
        // Arrange
        (RegisterRequest registerRequest, IdentityId identityId) = await RegisterAndScheduleDeletionAsync();
        string cancelToken = AccountDeletionEmailSpy.LastCancelToken!;

        HttpResponseMessage firstResponse = await SendCancelRequestAsync(registerRequest.Email, cancelToken);
        firstResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Act — the security stamp rotated on cancel, so the token is single-use
        HttpResponseMessage replayResponse = await SendCancelRequestAsync(registerRequest.Email, cancelToken);

        // Assert
        replayResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        ApplicationUser user = await GetUserAsync(identityId.Value);
        user.DeletionScheduledAt.ShouldBeNull();
    }

    private async Task<(RegisterRequest Request, IdentityId IdentityId)> RegisterAndScheduleDeletionAsync()
    {
        (RegisterRequest registerRequest, IdentityId identityId) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, TestPassword);

        string accessToken = await GetAccessTokenAsync(registerRequest.Email, TestPassword);

        DeleteAccountRequest deleteRequest = new(TestPassword);
        using HttpRequestMessage request = new(HttpMethod.Post, "auth/account/delete");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(deleteRequest);

        HttpResponseMessage response = await ApiClient.Http.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // The cancel token arrives through the pipeline (ADR-0038), not the request path
        await AccountDeletionEmailSpy.WaitForScheduledCaptureAsync();
        AccountDeletionEmailSpy.LastCancelToken.ShouldNotBeNullOrWhiteSpace();

        return (registerRequest, identityId);
    }

    private async Task<HttpResponseMessage> SendCancelRequestAsync(string email, string token)
    {
        CancelAccountDeletionRequest cancelRequest = new(email, token);

        return await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/account/cancel-deletion", UriKind.Relative), cancelRequest);
    }

    private async Task<HttpResponseMessage> RequestTokenAsync(string email, string password)
    {
        using FormUrlEncodedContent tokenRequest = new(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = email,
            ["password"] = password,
            ["client_id"] = "lotrokoniecdev-test",
            ["scope"] = "email profile roles api"
        });

        return await ApiClient.Http.PostAsync(new Uri("connect/token", UriKind.Relative), tokenRequest);
    }

    private async Task<ApplicationUser> GetUserAsync(Guid userId)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        return await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId);
    }
}
