using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using LotroKoniecDev.AuthSystem.API.Services.Emails;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Password;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.AuthSystem.Infrastructure.Emails;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

public sealed class EmailLinkFactoryTests : EndpointsTestBase
{
    // The trusted public origin configured for the test host (AuthSystemApiFactory -> OpenIddict:Issuer).
    private const string ConfiguredIssuerOrigin = "https://localhost:5002";

    public EmailLinkFactoryTests(AuthSystemApiFactory appFactory) : base(appFactory) { }

    [Fact]
    public void PasswordResetLink_IsBuiltFromConfiguredIssuerOrigin()
    {
        // Arrange
        using IServiceScope scope = Factory.Services.CreateScope();
        IPasswordResetLinkFactory linkFactory =
            scope.ServiceProvider.GetRequiredService<IPasswordResetLinkFactory>();

        // Act
        string link = linkFactory.Create("victim@example.com", "reset-token-123");

        // Assert
        link.ShouldStartWith($"{ConfiguredIssuerOrigin}/Account/ResetPassword");
    }

    [Fact]
    public void EmailVerificationLink_IsBuiltFromConfiguredIssuerOrigin()
    {
        // Arrange
        using IServiceScope scope = Factory.Services.CreateScope();
        IEmailVerificationLinkFactory linkFactory =
            scope.ServiceProvider.GetRequiredService<IEmailVerificationLinkFactory>();

        // Act
        string link = linkFactory.Create("victim@example.com", "verify-token-123");

        // Assert
        link.ShouldStartWith($"{ConfiguredIssuerOrigin}/Account/ConfirmEmail");
    }

    [Fact]
    public async Task ForgotPassword_BuildsResetLinkFromConfiguredIssuer_WhenRequestHostIsForged()
    {
        // Arrange — the suite's default spy replaces the whole sender and never invokes the real
        // link factory, so spin up a host running the real sender -> factory chain and capture the
        // outgoing email at the SMTP boundary. This drives the actual attack seam: a forged Host
        // header on the (anonymous) forgot-password request.
        const string forgedHost = "evil.attacker.com";
        SpyEmailService emailServiceSpy = new();

        using WebApplicationFactory<Program> hostWithRealSender = Factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IPasswordResetEmailSender>();
                services.AddScoped<IPasswordResetEmailSender, PasswordResetEmailSender>();

                services.RemoveAll<IEmailService>();
                services.AddSingleton<IEmailService>(emailServiceSpy);
            });
        });

        TestApiClient apiClient = new(hostWithRealSender.CreateClient(), ApiClient.JsonOptions);
        SpyAccountConfirmationEmailSender confirmationSpy =
            hostWithRealSender.Services.GetRequiredService<SpyAccountConfirmationEmailSender>();

        (RegisterRequest registerRequest, _) =
            await UserFactory.RegisterRandomUserUnconfirmedAsync(apiClient, Faker, confirmationSpy);

        apiClient.Http.DefaultRequestHeaders.Host = forgedHost;

        // Act
        HttpResponseMessage response = await apiClient.Http.PostAsJsonAsync(
            new Uri("auth/forgot-password", UriKind.Relative),
            new ForgotPasswordRequest(registerRequest.Email));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        emailServiceSpy.LastBody.ShouldNotBeNull();
        emailServiceSpy.LastBody.Html.ShouldContain($"{ConfiguredIssuerOrigin}/Account/ResetPassword");
        emailServiceSpy.LastBody.Html.ShouldNotContain(forgedHost);
        emailServiceSpy.LastBody.PlainText.ShouldContain($"{ConfiguredIssuerOrigin}/Account/ResetPassword");
        emailServiceSpy.LastBody.PlainText.ShouldNotContain(forgedHost);
    }
}
