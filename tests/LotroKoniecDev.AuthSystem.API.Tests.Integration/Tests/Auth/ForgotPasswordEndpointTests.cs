using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Password;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;

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
    public async Task ForgotPassword_ShouldSendEmail_WhenUserExists()
    {
        // Arrange
        PasswordResetEmailSpy.Reset();

        (RegisterRequest request, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy);

        ForgotPasswordRequest forgotRequest = new(request.Email);

        // Act
        await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/forgot-password", UriKind.Relative), forgotRequest);

        // Assert
        PasswordResetEmailSpy.CallCount.ShouldBe(1);
        PasswordResetEmailSpy.LastEmail.ShouldBe(request.Email);
        PasswordResetEmailSpy.LastResetToken.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task ForgotPassword_ShouldNotSendEmail_WhenUserDoesNotExist()
    {
        // Arrange
        PasswordResetEmailSpy.Reset();

        ForgotPasswordRequest forgotRequest = new("nobody@example.com");

        // Act
        await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/forgot-password", UriKind.Relative), forgotRequest);

        // Assert
        PasswordResetEmailSpy.CallCount.ShouldBe(0);
    }
}
