using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using LotroKoniecDev.AuthSystem.API.Services.ResponseTiming;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.EmailConfirmation;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Password;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

/// <summary>
/// Every answer that hides whether an address has an account leaves no sooner than its floor, on every
/// branch a caller can reach without the password or the mailed token (ADR-0059). The rest of the suite
/// runs with a floor that never waits, so these tests are the only ones that would see a member stop
/// waiting.
/// Each test sends its branches at the same time, so a member costs one floor, not one per branch. The
/// branches are arranged one after another, because the registration helpers share one mail spy.
/// </summary>
public sealed partial class ResponseTimeFloorEndpointTests : EndpointsTestBase
{
    private const string WrongToken = "not-the-mailed-token";

    public ResponseTimeFloorEndpointTests(AuthSystemApiFactory appFactory) : base(appFactory) { }

    [Fact]
    public async Task LoginPage_ShouldWaitForTheFloor_OnEveryAnswerThatShowsTheGeneralMessage()
    {
        // Arrange
        WebApplicationFactory<Program> host = await Factory.GetResponseTimeFloorHostAsync();
        (RegisterRequest wrongPassword, _) = await RegisterConfirmedAsync();
        (RegisterRequest lockedOut, _) = await RegisterConfirmedAsync();
        await LockOutAsync(lockedOut.Email);
        (RegisterRequest passwordless, _) = await RegisterConfirmedAsync();
        await RemovePasswordAsync(passwordless.Email);
        (RegisterRequest deletionScheduled, _) = await RegisterConfirmedAsync();
        await ScheduleDeletionAsync(deletionScheduled.Email);

        // Act
        IReadOnlyList<BranchAnswer> answers = await CollectAsync(new()
        {
            ["unknown address"] = PostLoginAsync(host, UnknownAddress(), "WhateverPass1!"),
            ["wrong password"] = PostLoginAsync(host, wrongPassword.Email, wrongPassword.Password + "WRONG"),
            ["locked out"] = PostLoginAsync(host, lockedOut.Email, lockedOut.Password),
            ["no password"] = PostLoginAsync(host, passwordless.Email, passwordless.Password),
            ["deletion scheduled, wrong password"] =
                PostLoginAsync(host, deletionScheduled.Email, deletionScheduled.Password + "WRONG")
        });

        // Assert
        answers.ShouldAllBe(answer => answer.Elapsed >= ResponseTimeFloors.AccountLookup);
    }

    [Fact]
    public async Task ForgotPasswordPage_ShouldWaitForTheFloor_WhetherOrNotTheAddressHasAnAccount()
    {
        // Arrange
        WebApplicationFactory<Program> host = await Factory.GetResponseTimeFloorHostAsync();
        (RegisterRequest account, _) = await RegisterConfirmedAsync();

        // Act
        IReadOnlyList<BranchAnswer> answers = await CollectAsync(new()
        {
            ["unknown address"] = PostPageAsync(host, "/Account/ForgotPassword", new() { ["Email"] = UnknownAddress() }),
            ["real account"] = PostPageAsync(host, "/Account/ForgotPassword", new() { ["Email"] = account.Email })
        });

        // Assert
        answers.ShouldAllBe(answer => answer.Elapsed >= ResponseTimeFloors.AccountLookup);
    }

    [Fact]
    public async Task ForgotPasswordEndpoint_ShouldWaitForTheFloor_WhetherOrNotTheAddressHasAnAccount()
    {
        // Arrange
        WebApplicationFactory<Program> host = await Factory.GetResponseTimeFloorHostAsync();
        (RegisterRequest account, _) = await RegisterConfirmedAsync();

        // Act
        IReadOnlyList<BranchAnswer> answers = await CollectAsync(new()
        {
            ["unknown address"] = PostJsonAsync(host, "auth/forgot-password", new ForgotPasswordRequest(UnknownAddress())),
            ["real account"] = PostJsonAsync(host, "auth/forgot-password", new ForgotPasswordRequest(account.Email))
        });

        // Assert
        answers.ShouldAllBe(answer => answer.Elapsed >= ResponseTimeFloors.AccountLookup);
    }

    [Fact]
    public async Task ResetPasswordPage_ShouldWaitForTheFloor_OnEveryBranchReachableWithoutTheToken()
    {
        // Arrange
        WebApplicationFactory<Program> host = await Factory.GetResponseTimeFloorHostAsync();
        (RegisterRequest account, _) = await RegisterConfirmedAsync();
        (RegisterRequest deletionScheduled, _) = await RegisterConfirmedAsync();
        await ScheduleDeletionAsync(deletionScheduled.Email);

        // Act
        IReadOnlyList<BranchAnswer> answers = await CollectAsync(new()
        {
            ["unknown address"] = PostResetPageAsync(host, UnknownAddress()),
            ["real account, wrong token"] = PostResetPageAsync(host, account.Email),
            ["deletion scheduled"] = PostResetPageAsync(host, deletionScheduled.Email)
        });

        // Assert
        answers.ShouldAllBe(answer => answer.Elapsed >= ResponseTimeFloors.AccountLookup);
    }

    [Fact]
    public async Task ResetPasswordEndpoint_ShouldWaitForTheFloor_OnEveryBranchReachableWithoutTheToken()
    {
        // Arrange
        WebApplicationFactory<Program> host = await Factory.GetResponseTimeFloorHostAsync();
        (RegisterRequest account, _) = await RegisterConfirmedAsync();
        (RegisterRequest deletionScheduled, _) = await RegisterConfirmedAsync();
        await ScheduleDeletionAsync(deletionScheduled.Email);

        // Act
        IReadOnlyList<BranchAnswer> answers = await CollectAsync(new()
        {
            ["unknown address"] = PostJsonAsync(host, "auth/reset-password", ResetRequest(UnknownAddress())),
            ["real account, wrong token"] = PostJsonAsync(host, "auth/reset-password", ResetRequest(account.Email)),
            ["deletion scheduled"] = PostJsonAsync(host, "auth/reset-password", ResetRequest(deletionScheduled.Email))
        });

        // Assert
        answers.ShouldAllBe(answer => answer.Elapsed >= ResponseTimeFloors.AccountLookup);
    }

    [Fact]
    public async Task ConfirmEmailPage_ShouldWaitForTheFloor_OnEveryBranchReachableWithoutTheToken()
    {
        // Arrange
        WebApplicationFactory<Program> host = await Factory.GetResponseTimeFloorHostAsync();
        (RegisterRequest unconfirmed, _) = await RegisterUnconfirmedAsync();
        (RegisterRequest confirmed, _) = await RegisterConfirmedAsync();

        // Act
        IReadOnlyList<BranchAnswer> answers = await CollectAsync(new()
        {
            ["unknown address"] = GetConfirmEmailPageAsync(host, UnknownAddress()),
            ["unconfirmed, wrong token"] = GetConfirmEmailPageAsync(host, unconfirmed.Email),
            ["confirmed, wrong token"] = GetConfirmEmailPageAsync(host, confirmed.Email)
        });

        // Assert
        answers.ShouldAllBe(answer => answer.Elapsed >= ResponseTimeFloors.AccountLookup);
    }

    [Fact]
    public async Task ConfirmEmailEndpoint_ShouldWaitForTheFloor_OnEveryBranchReachableWithoutTheToken()
    {
        // Arrange
        WebApplicationFactory<Program> host = await Factory.GetResponseTimeFloorHostAsync();
        (RegisterRequest unconfirmed, _) = await RegisterUnconfirmedAsync();
        (RegisterRequest confirmed, _) = await RegisterConfirmedAsync();

        // Act
        IReadOnlyList<BranchAnswer> answers = await CollectAsync(new()
        {
            ["unknown address"] =
                PostJsonAsync(host, "auth/confirm-email", new ConfirmEmailRequest(UnknownAddress(), WrongToken)),
            ["unconfirmed, wrong token"] =
                PostJsonAsync(host, "auth/confirm-email", new ConfirmEmailRequest(unconfirmed.Email, WrongToken)),
            ["confirmed"] =
                PostJsonAsync(host, "auth/confirm-email", new ConfirmEmailRequest(confirmed.Email, WrongToken))
        });

        // Assert
        answers.ShouldAllBe(answer => answer.Elapsed >= ResponseTimeFloors.AccountLookup);
    }

    [Fact]
    public async Task CancelDeletionEndpoint_ShouldWaitForTheFloor_OnEveryBranchReachableWithoutTheToken()
    {
        // Arrange
        WebApplicationFactory<Program> host = await Factory.GetResponseTimeFloorHostAsync();
        (RegisterRequest account, _) = await RegisterConfirmedAsync();
        (RegisterRequest deletionScheduled, _) = await RegisterConfirmedAsync();
        await ScheduleDeletionAsync(deletionScheduled.Email);

        // Act
        IReadOnlyList<BranchAnswer> answers = await CollectAsync(new()
        {
            ["unknown address"] = PostJsonAsync(
                host, "auth/account/cancel-deletion", new CancelAccountDeletionRequest(UnknownAddress(), WrongToken)),
            ["no deletion scheduled"] = PostJsonAsync(
                host, "auth/account/cancel-deletion", new CancelAccountDeletionRequest(account.Email, WrongToken)),
            ["deletion scheduled, wrong token"] = PostJsonAsync(
                host, "auth/account/cancel-deletion", new CancelAccountDeletionRequest(deletionScheduled.Email, WrongToken))
        });

        // Assert
        answers.ShouldAllBe(answer => answer.Elapsed >= ResponseTimeFloors.AccountLookup);
    }

    /// <summary>
    /// The resend page calls the same handler, so the endpoint covers both. The unconfirmed branch is the
    /// one that sends a mail; here the sender is a spy that returns at once.
    /// </summary>
    [Fact]
    public async Task ResendConfirmationEndpoint_ShouldWaitForTheLongerFloor_OnEveryBranch()
    {
        // Arrange
        WebApplicationFactory<Program> host = await Factory.GetResponseTimeFloorHostAsync();
        (RegisterRequest unconfirmed, _) = await RegisterUnconfirmedAsync();
        (RegisterRequest confirmed, _) = await RegisterConfirmedAsync();

        // Act
        IReadOnlyList<BranchAnswer> answers = await CollectAsync(new()
        {
            ["unknown address"] = PostJsonAsync(
                host, "auth/resend-email-confirmation", new ResendEmailConfirmationRequest(UnknownAddress())),
            ["unconfirmed"] = PostJsonAsync(
                host, "auth/resend-email-confirmation", new ResendEmailConfirmationRequest(unconfirmed.Email)),
            ["confirmed"] = PostJsonAsync(
                host, "auth/resend-email-confirmation", new ResendEmailConfirmationRequest(confirmed.Email))
        });

        // Assert
        answers.ShouldAllBe(answer => answer.Elapsed >= ResponseTimeFloors.LiveMailSend);
    }

    private static async Task<IReadOnlyList<BranchAnswer>> CollectAsync(Dictionary<string, Task<Answer>> pending)
    {
        List<BranchAnswer> answers = [];
        foreach ((string branch, Task<Answer> answer) in pending)
        {
            Answer completed = await answer;
            answers.Add(new BranchAnswer(branch, completed.Status, completed.Elapsed));
        }

        return answers;
    }

    private static async Task<Answer> PostLoginAsync(WebApplicationFactory<Program> host, string email, string password) =>
        await PostPageAsync(host, "/Account/Login", new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = password
        });

    private static async Task<Answer> PostResetPageAsync(WebApplicationFactory<Program> host, string email) =>
        await PostPageAsync(host, "/Account/ResetPassword", new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Token"] = WrongToken,
            ["NewPassword"] = "NewPass99!",
            ["ConfirmPassword"] = "NewPass99!"
        });

    /// <summary>
    /// Fetches the page first for its antiforgery token and cookie, and times only the POST.
    /// </summary>
    private static async Task<Answer> PostPageAsync(
        WebApplicationFactory<Program> host,
        string page,
        Dictionary<string, string> formFields)
    {
        using HttpClient browser = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using HttpResponseMessage pageResponse = await browser.GetAsync(new Uri(page, UriKind.Relative));
        string? antiForgeryToken = ExtractAntiForgeryToken(await pageResponse.Content.ReadAsStringAsync());
        if (antiForgeryToken is not null)
        {
            formFields["__RequestVerificationToken"] = antiForgeryToken;
        }

        using FormUrlEncodedContent content = new(formFields);
        return await TimeAsync(() => browser.PostAsync(new Uri(page, UriKind.Relative), content));
    }

    private static async Task<Answer> GetConfirmEmailPageAsync(WebApplicationFactory<Program> host, string email)
    {
        using HttpClient browser = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Uri page = new(
            $"/Account/ConfirmEmail?email={Uri.EscapeDataString(email)}&token={Uri.EscapeDataString(WrongToken)}",
            UriKind.Relative);

        return await TimeAsync(() => browser.GetAsync(page));
    }

    private static async Task<Answer> PostJsonAsync<TRequest>(
        WebApplicationFactory<Program> host,
        string path,
        TRequest request)
    {
        using HttpClient client = host.CreateClient();
        return await TimeAsync(() => client.PostAsJsonAsync(new Uri(path, UriKind.Relative), request));
    }

    private static async Task<Answer> TimeAsync(Func<Task<HttpResponseMessage>> send)
    {
        long startedAt = Stopwatch.GetTimestamp();
        using HttpResponseMessage response = await send();
        return new Answer(response.StatusCode, Stopwatch.GetElapsedTime(startedAt));
    }

    private static ResetPasswordRequest ResetRequest(string email) => new(email, WrongToken, "NewPass99!");

    private string UnknownAddress() => "nobody-" + Faker.Random.AlphaNumeric(8) + "@example.com";

    private async Task<(RegisterRequest Request, IdentityId Id)> RegisterConfirmedAsync() =>
        await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy);

    private async Task<(RegisterRequest Request, IdentityId Id)> RegisterUnconfirmedAsync() =>
        await UserFactory.RegisterRandomUserUnconfirmedAsync(ApiClient, Faker, AccountConfirmationEmailSpy);

    private async Task LockOutAsync(string email)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        UserManager<ApplicationUser> userManager =
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        ApplicationUser user = await userManager.FindByEmailAsync(email)
            ?? throw new InvalidOperationException($"Test user '{email}' was not found.");

        await userManager.SetLockoutEnabledAsync(user, true);
        await userManager.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddMinutes(30));
    }

    private async Task RemovePasswordAsync(string email)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        UserManager<ApplicationUser> userManager =
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        ApplicationUser user = await userManager.FindByEmailAsync(email)
            ?? throw new InvalidOperationException($"Test user '{email}' was not found.");

        IdentityResult result = await userManager.RemovePasswordAsync(user);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Could not remove the password of test user '{email}'.");
        }
    }

    /// <summary>
    /// Only the field the branches read: each of them checks <c>DeletionScheduledAt</c> before anything else.
    /// </summary>
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

    private static string? ExtractAntiForgeryToken(string html)
    {
        Match match = AntiForgeryTokenRegex().Match(html);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex("""name="__RequestVerificationToken".*?value="([^"]+)""")]
    private static partial Regex AntiForgeryTokenRegex();

    private sealed record Answer(HttpStatusCode Status, TimeSpan Elapsed);

    /// <summary>
    /// What a failed assertion prints: which branch answered, with what, and how soon.
    /// </summary>
    private sealed record BranchAnswer(string Branch, HttpStatusCode Status, TimeSpan Elapsed);
}
