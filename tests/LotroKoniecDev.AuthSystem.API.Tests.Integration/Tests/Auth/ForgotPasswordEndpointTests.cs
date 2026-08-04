using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Password;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.AuthSystem.Persistence.Outbox;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

public sealed class ForgotPasswordEndpointTests : EndpointsTestBase
{
    public ForgotPasswordEndpointTests(AuthSystemApiFactory appFactory) : base(appFactory) { }

    [Fact]
    public async Task ForgotPassword_ShouldReturnOk_WhenEmailExists()
    {
        // Arrange
        (RegisterRequest request, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy);

        ForgotPasswordRequest forgotRequest = new(request.Email);

        // Act
        HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/forgot-password", UriKind.Relative), forgotRequest);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ForgotPassword_ShouldReturnOk_WhenEmailDoesNotExist()
    {
        // Arrange — prevent email enumeration by always returning 200
        ForgotPasswordRequest forgotRequest = new("nonexistent@example.com");

        // Act
        HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/forgot-password", UriKind.Relative), forgotRequest);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ForgotPassword_ShouldReturnBadRequest_WhenEmailIsInvalid()
    {
        // Arrange
        ForgotPasswordRequest forgotRequest = new("not-an-email");

        // Act
        HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/forgot-password", UriKind.Relative), forgotRequest);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ForgotPassword_ShouldDeliverEmail_WhenUserExists()
    {
        // Arrange
        PasswordResetEmailSpy.Reset();

        (RegisterRequest request, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy);

        ForgotPasswordRequest forgotRequest = new(request.Email);

        // Act — the request only commits the outbox row; the e-mail arrives through the
        // pipeline (relay -> delivery -> spy), so the capture has to be awaited (ADR-0038)
        await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/forgot-password", UriKind.Relative), forgotRequest);
        await PasswordResetEmailSpy.WaitForCaptureAsync();

        // Assert
        PasswordResetEmailSpy.CallCount.ShouldBe(1);
        PasswordResetEmailSpy.LastEmail.ShouldBe(request.Email);
        PasswordResetEmailSpy.LastResetToken.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task ForgotPassword_ShouldWriteAnIdOnlyOutboxRow_WhenUserExists()
    {
        // Arrange
        PasswordResetEmailSpy.Reset();

        (RegisterRequest request, IdentityId identityId) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy);

        // Act
        await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/forgot-password", UriKind.Relative), new ForgotPasswordRequest(request.Email));
        await PasswordResetEmailSpy.WaitForCaptureAsync();

        // Assert — the payload carries the user id and nothing else: the reset token is minted
        // at delivery and must never persist in an outbox row (ADR-0038 decision 2)
        OutboxMessage? outboxRow = await OutboxAssertions.WaitForOutboxRowAsync(
            Factory, row => row.Type == nameof(PasswordResetRequested));
        outboxRow.ShouldNotBeNull();
        PasswordResetRequested payload = JsonSerializer.Deserialize<PasswordResetRequested>(outboxRow.Payload)
            .ShouldNotBeNull();
        payload.IdentityUserId.ShouldBe(identityId.Value);
        PasswordResetEmailSpy.LastResetToken.ShouldNotBeNullOrEmpty();
        outboxRow.Payload.ShouldNotContain(PasswordResetEmailSpy.LastResetToken);
    }

    [Fact]
    public async Task ForgotPassword_ShouldNotWriteAnOutboxRow_WhenUserDoesNotExist()
    {
        // Arrange
        PasswordResetEmailSpy.Reset();

        ForgotPasswordRequest forgotRequest = new("nobody@example.com");

        // Act
        await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/forgot-password", UriKind.Relative), forgotRequest);

        // Assert — the row is written inside the request, so its absence after the response is
        // definitive: nothing was queued, nothing will ever be delivered
        OutboxMessage? outboxRow = await OutboxAssertions.WaitForOutboxRowAsync(
            Factory, row => row.Type == nameof(PasswordResetRequested), TimeSpan.FromSeconds(1));
        outboxRow.ShouldBeNull();
        PasswordResetEmailSpy.CallCount.ShouldBe(0);
    }
}
