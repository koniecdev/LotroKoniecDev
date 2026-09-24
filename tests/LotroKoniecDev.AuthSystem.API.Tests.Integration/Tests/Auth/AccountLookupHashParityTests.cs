using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.EmailConfirmation;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Password;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

/// <summary>
/// The second layer under the time floor (ADR-0059 §5): when a branch runs past the floor, the branches
/// must still cost the same CPU, so each one verifies exactly one password hash. These branches used to
/// skip the dummy hash for a real account and answered faster than an unknown address. Login and forgot
/// password are pinned elsewhere.
/// </summary>
public sealed partial class AccountLookupHashParityTests : EndpointsTestBase
{
    private const string WrongToken = "not-the-mailed-token";

    public AccountLookupHashParityTests(AuthSystemApiFactory appFactory) : base(appFactory) { }

    [Theory]
    [InlineData("unknown address")]
    [InlineData("unconfirmed")]
    [InlineData("confirmed")]
    public async Task ResendEmailConfirmation_ShouldVerifyExactlyOnePasswordHash_OnEveryBranch(string branch)
    {
        // Arrange
        string email = await ArrangeAddressAsync(branch);
        SpyPasswordHasher passwordHasher = ResetPasswordHasherSpy();

        // Act
        HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/resend-email-confirmation", UriKind.Relative),
            new ResendEmailConfirmationRequest(email));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        passwordHasher.VerifyCount.ShouldBe(1);
    }

    [Theory]
    [InlineData("unknown address")]
    [InlineData("confirmed")]
    [InlineData("deletion scheduled")]
    public async Task ResetPasswordEndpoint_ShouldVerifyExactlyOnePasswordHash_OnEveryBranchReachableWithoutTheToken(
        string branch)
    {
        // Arrange
        string email = await ArrangeAddressAsync(branch);
        SpyPasswordHasher passwordHasher = ResetPasswordHasherSpy();

        // Act
        HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/reset-password", UriKind.Relative),
            new ResetPasswordRequest(email, WrongToken, "NewPass99!"));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        passwordHasher.VerifyCount.ShouldBe(1);
    }

    [Theory]
    [InlineData("unknown address")]
    [InlineData("confirmed")]
    [InlineData("deletion scheduled")]
    public async Task ResetPasswordPage_ShouldVerifyExactlyOnePasswordHash_OnEveryBranchReachableWithoutTheToken(
        string branch)
    {
        // Arrange
        string email = await ArrangeAddressAsync(branch);
        using HttpClient browser = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Dictionary<string, string> form = await PrepareFormAsync(browser, "/Account/ResetPassword", new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Token"] = WrongToken,
            ["NewPassword"] = "NewPass99!",
            ["ConfirmPassword"] = "NewPass99!"
        });
        SpyPasswordHasher passwordHasher = ResetPasswordHasherSpy();

        // Act
        using FormUrlEncodedContent content = new(form);
        HttpResponseMessage response = await browser.PostAsync(new Uri("/Account/ResetPassword", UriKind.Relative), content);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        passwordHasher.VerifyCount.ShouldBe(1);
    }

    [Theory]
    [InlineData("unknown address")]
    [InlineData("unconfirmed")]
    [InlineData("confirmed")]
    public async Task ConfirmEmailEndpoint_ShouldVerifyExactlyOnePasswordHash_OnEveryBranchReachableWithoutTheToken(
        string branch)
    {
        // Arrange
        string email = await ArrangeAddressAsync(branch);
        SpyPasswordHasher passwordHasher = ResetPasswordHasherSpy();

        // Act
        HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/confirm-email", UriKind.Relative),
            new ConfirmEmailRequest(email, WrongToken));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        passwordHasher.VerifyCount.ShouldBe(1);
    }

    [Theory]
    [InlineData("unknown address")]
    [InlineData("unconfirmed")]
    [InlineData("confirmed")]
    public async Task ConfirmEmailPage_ShouldVerifyExactlyOnePasswordHash_OnEveryBranchReachableWithoutTheToken(
        string branch)
    {
        // Arrange
        string email = await ArrangeAddressAsync(branch);
        using HttpClient browser = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        SpyPasswordHasher passwordHasher = ResetPasswordHasherSpy();

        // Act
        HttpResponseMessage response = await browser.GetAsync(new Uri(
            $"/Account/ConfirmEmail?email={Uri.EscapeDataString(email)}&token={Uri.EscapeDataString(WrongToken)}",
            UriKind.Relative));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        passwordHasher.VerifyCount.ShouldBe(1);
    }

    private async Task<string> ArrangeAddressAsync(string branch)
    {
        switch (branch)
        {
            case "unknown address":
                return "nobody-" + Faker.Random.AlphaNumeric(8) + "@example.com";
            case "unconfirmed":
                (RegisterRequest unconfirmed, _) =
                    await UserFactory.RegisterRandomUserUnconfirmedAsync(ApiClient, Faker, AccountConfirmationEmailSpy);
                return unconfirmed.Email;
            case "confirmed":
                (RegisterRequest confirmed, _) =
                    await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy);
                return confirmed.Email;
            case "deletion scheduled":
                (RegisterRequest scheduled, _) =
                    await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy);
                await ScheduleDeletionAsync(scheduled.Email);
                return scheduled.Email;
            default:
                throw new ArgumentOutOfRangeException(nameof(branch), branch, null);
        }
    }

    private SpyPasswordHasher ResetPasswordHasherSpy()
    {
        SpyPasswordHasher passwordHasher = Factory.Services.GetRequiredService<SpyPasswordHasher>();
        passwordHasher.Reset();
        return passwordHasher;
    }

    private async Task ScheduleDeletionAsync(string email)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        UserManager<ApplicationUser> userManager =
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        ApplicationUser user = await userManager.FindByEmailAsync(email)
            ?? throw new InvalidOperationException($"Test user '{email}' was not found.");

        user.DeletionScheduledAt = DateTimeOffset.UtcNow;
        IdentityResult result = await userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Could not schedule the deletion of test user '{email}'.");
        }
    }

    private static async Task<Dictionary<string, string>> PrepareFormAsync(
        HttpClient browser,
        string page,
        Dictionary<string, string> formFields)
    {
        using HttpResponseMessage pageResponse = await browser.GetAsync(new Uri(page, UriKind.Relative));
        string? antiForgeryToken = ExtractAntiForgeryToken(await pageResponse.Content.ReadAsStringAsync());
        if (antiForgeryToken is not null)
        {
            formFields["__RequestVerificationToken"] = antiForgeryToken;
        }

        return formFields;
    }

    private static string? ExtractAntiForgeryToken(string html)
    {
        Match match = AntiForgeryTokenRegex().Match(html);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex("""name="__RequestVerificationToken".*?value="([^"]+)""")]
    private static partial Regex AntiForgeryTokenRegex();
}
