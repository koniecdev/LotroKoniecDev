using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.EmailConfirmation;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

public sealed class ResendEmailConfirmationEndpointTests : EndpointsTestBase
{
    public ResendEmailConfirmationEndpointTests(AuthSystemApiFactory appFactory) : base(appFactory) { }

    [Fact]
    public async Task ResendEmailConfirmation_ShouldReturnOk_WhenEmailExists()
    {
        // Arrange
        (RegisterRequest request, _) =
            await UserFactory.RegisterRandomUserUnconfirmedAsync(ApiClient, Faker, AccountConfirmationEmailSpy);

        AccountConfirmationEmailSpy.Reset();

        ResendEmailConfirmationRequest resendRequest = new(request.Email);

        // Act
        HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/resend-email-confirmation", UriKind.Relative), resendRequest);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ResendEmailConfirmation_ShouldReturnOk_WhenEmailDoesNotExist()
    {
        // Arrange: prevent email enumeration by always returning 200
        ResendEmailConfirmationRequest resendRequest = new("nonexistent@example.com");

        // Act
        HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/resend-email-confirmation", UriKind.Relative), resendRequest);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ResendEmailConfirmation_ShouldReturnBadRequest_WhenEmailIsInvalid()
    {
        // Arrange
        ResendEmailConfirmationRequest resendRequest = new("not-an-email");

        // Act
        HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/resend-email-confirmation", UriKind.Relative), resendRequest);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ResendEmailConfirmation_ShouldReturnBadRequest_WhenEmailIsEmpty()
    {
        // Arrange
        ResendEmailConfirmationRequest resendRequest = new("");

        // Act
        HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/resend-email-confirmation", UriKind.Relative), resendRequest);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ResendEmailConfirmation_ShouldSendEmail_WhenUserExistsAndNotConfirmed()
    {
        // Arrange
        (RegisterRequest request, _) =
            await UserFactory.RegisterRandomUserUnconfirmedAsync(ApiClient, Faker, AccountConfirmationEmailSpy);

        AccountConfirmationEmailSpy.Reset();

        ResendEmailConfirmationRequest resendRequest = new(request.Email);

        // Act
        await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/resend-email-confirmation", UriKind.Relative), resendRequest);

        // Assert
        AccountConfirmationEmailSpy.CallCount.ShouldBe(1);
        AccountConfirmationEmailSpy.LastEmail.ShouldBe(request.Email);
        AccountConfirmationEmailSpy.LastConfirmationToken.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task ResendEmailConfirmation_ShouldNotSendEmail_WhenUserDoesNotExist()
    {
        // Arrange
        AccountConfirmationEmailSpy.Reset();

        ResendEmailConfirmationRequest resendRequest = new("nobody@example.com");

        // Act
        await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/resend-email-confirmation", UriKind.Relative), resendRequest);

        // Assert
        AccountConfirmationEmailSpy.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task ResendEmailConfirmation_ShouldNotSendEmail_WhenEmailAlreadyConfirmed()
    {
        // Arrange
        (RegisterRequest request, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy);

        AccountConfirmationEmailSpy.Reset();

        ResendEmailConfirmationRequest resendRequest = new(request.Email);

        // Act
        await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/resend-email-confirmation", UriKind.Relative), resendRequest);

        // Assert
        AccountConfirmationEmailSpy.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task ResendEmailConfirmation_ShouldGenerateValidToken_ThatCanBeUsedToConfirm()
    {
        // Arrange
        (RegisterRequest request, _) =
            await UserFactory.RegisterRandomUserUnconfirmedAsync(ApiClient, Faker, AccountConfirmationEmailSpy);

        AccountConfirmationEmailSpy.Reset();

        // Resend confirmation email
        await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/resend-email-confirmation", UriKind.Relative),
            new ResendEmailConfirmationRequest(request.Email));

        // Act: use the new token to confirm
        ConfirmEmailRequest confirmRequest = new(AccountConfirmationEmailSpy.LastEmail!, AccountConfirmationEmailSpy.LastConfirmationToken!);

        HttpResponseMessage confirmResponse = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/confirm-email", UriKind.Relative), confirmRequest);

        // Assert
        confirmResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ResendEmailConfirmation_ShouldReturnOk_WhenEmailAlreadyConfirmed()
    {
        // Arrange: prevent email enumeration by always returning 200, even for confirmed emails
        (RegisterRequest request, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy);

        ResendEmailConfirmationRequest resendRequest = new(request.Email);

        // Act
        HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/resend-email-confirmation", UriKind.Relative), resendRequest);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ResendEmailConfirmation_AfterUsed_ShouldNotAllowOldTokenConfirmation()
    {
        // Arrange: register and capture the original token from registration
        (RegisterRequest request, _) =
            await UserFactory.RegisterRandomUserUnconfirmedAsync(ApiClient, Faker, AccountConfirmationEmailSpy);

        string originalToken = AccountConfirmationEmailSpy.LastConfirmationToken!;
        AccountConfirmationEmailSpy.Reset();

        // Resend to get a new token
        await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/resend-email-confirmation", UriKind.Relative),
            new ResendEmailConfirmationRequest(request.Email));

        string newToken = AccountConfirmationEmailSpy.LastConfirmationToken!;

        // Confirm with the new token
        HttpResponseMessage confirmResponse = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/confirm-email", UriKind.Relative),
            new ConfirmEmailRequest(request.Email, newToken));
        confirmResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Act: try the original token after email is already confirmed
        HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/confirm-email", UriKind.Relative),
            new ConfirmEmailRequest(request.Email, originalToken));

        // Assert: should fail because email is already confirmed
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ResendEmailConfirmation_ShouldSendDirectlyAndWriteNoOutboxRow_WhenBrokerIsDown()
    {
        // Arrange: resend is the deliberate escape hatch of ADR-0038 decision 4: it must keep
        // delivering precisely when the pipeline is the thing that is broken, so the spy broker
        // refuses every publish for the duration of the act.
        (RegisterRequest request, _) =
            await UserFactory.RegisterRandomUserUnconfirmedAsync(ApiClient, Faker, AccountConfirmationEmailSpy);

        AccountConfirmationEmailSpy.Reset();

        SpyMessagePublisher messagePublisherSpy = Factory.Services.GetRequiredService<SpyMessagePublisher>();
        int outboxRowsBeforeResend = await CountOutboxRowsAsync();

        messagePublisherSpy.FailWith = new InvalidOperationException("broker down");

        try
        {
            // Act
            HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(
                new Uri("auth/resend-email-confirmation", UriKind.Relative),
                new ResendEmailConfirmationRequest(request.Email));

            // Assert: the e-mail was captured before the response returned (in-request, direct
            // path) and the outbox grew by nothing: with zero new rows and a refusing broker, the
            // pipeline cannot be what delivered it. A refactor routing resend through the outbox
            // flips both assertions.
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            AccountConfirmationEmailSpy.CallCount.ShouldBe(1);
            AccountConfirmationEmailSpy.LastEmail.ShouldBe(request.Email);
            (await CountOutboxRowsAsync()).ShouldBe(outboxRowsBeforeResend);
        }
        finally
        {
            messagePublisherSpy.FailWith = null;
        }
    }

    private async Task<int> CountOutboxRowsAsync()
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        return await db.OutboxMessages.AsNoTracking().CountAsync();
    }
}
