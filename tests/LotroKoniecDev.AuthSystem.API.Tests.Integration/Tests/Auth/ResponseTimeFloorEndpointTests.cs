using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using LotroKoniecDev.AuthSystem.API.Services.ResponseTiming;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.EmailConfirmation;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Password;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

/// <summary>
/// Every answer that hides whether an address has an account leaves no sooner than its floor, on every
/// branch reachable without the password or the mailed token (ADR-0059). The rest of the suite runs with a
/// floor that never waits. Each test sends its branches at once, so it costs one floor; they are arranged
/// one after another, because the registration helpers share one mail spy.
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
        await AccountStateFactory.LockOutAsync(Factory.Services, lockedOut.Email);
        (RegisterRequest passwordless, _) = await RegisterConfirmedAsync();
        await AccountStateFactory.RemovePasswordAsync(Factory.Services, passwordless.Email);
        (RegisterRequest deletionScheduled, _) = await RegisterConfirmedAsync();
        await AccountStateFactory.ScheduleDeletionAsync(Factory.Services, deletionScheduled.Email);
        (RegisterRequest budgetSpent, _) = await RegisterConfirmedAsync();
        await AccountStateFactory.ScheduleDeletionAsync(Factory.Services, budgetSpent.Email);
        await AccountStateFactory.SpendDeletionScheduledLoginBudgetAsync(host.Services, budgetSpent.Email);

        // Act
        IReadOnlyList<BranchAnswer> answers = await CollectAsync(new()
        {
            ["unknown address"] = PostLoginAsync(host, UnknownAddress(), "WhateverPass1!"),
            ["wrong password"] = PostLoginAsync(host, wrongPassword.Email, wrongPassword.Password + "WRONG"),
            ["locked out"] = PostLoginAsync(host, lockedOut.Email, lockedOut.Password),
            ["no password"] = PostLoginAsync(host, passwordless.Email, passwordless.Password),
            ["deletion scheduled, wrong password"] =
                PostLoginAsync(host, deletionScheduled.Email, deletionScheduled.Password + "WRONG"),
            ["deletion scheduled, login budget spent, right password"] =
                PostLoginAsync(host, budgetSpent.Email, budgetSpent.Password)
        });

        // Assert
        answers.ShouldAllBe(answer => answer.Elapsed >= ResponseTimeFloors.AccountLookup);
        answers.ShouldAllBe(answer => answer.Status == HttpStatusCode.OK);
    }

    [Fact]
    public async Task ForgotPasswordPage_ShouldWaitForTheFloor_WhetherOrNotTheAddressHasAnAccount()
    {
        // Arrange
        WebApplicationFactory<Program> host = await Factory.GetResponseTimeFloorHostAsync();
        (RegisterRequest account, _) = await RegisterConfirmedAsync();
        (RegisterRequest budgetSpent, _) = await RegisterConfirmedAsync();
        await AccountStateFactory.SpendPasswordResetBudgetAsync(host.Services, budgetSpent.Email);

        // Act
        IReadOnlyList<BranchAnswer> answers = await CollectAsync(new()
        {
            ["unknown address"] = PostPageAsync(host, "/Account/ForgotPassword", new() { ["Email"] = UnknownAddress() }),
            ["real account"] = PostPageAsync(host, "/Account/ForgotPassword", new() { ["Email"] = account.Email }),
            ["real account, budget spent"] =
                PostPageAsync(host, "/Account/ForgotPassword", new() { ["Email"] = budgetSpent.Email })
        });

        // Assert
        answers.ShouldAllBe(answer => answer.Elapsed >= ResponseTimeFloors.AccountLookup);
        answers.ShouldAllBe(answer => answer.Status == HttpStatusCode.OK);
    }

    [Fact]
    public async Task ForgotPasswordEndpoint_ShouldWaitForTheFloor_WhetherOrNotTheAddressHasAnAccount()
    {
        // Arrange
        WebApplicationFactory<Program> host = await Factory.GetResponseTimeFloorHostAsync();
        (RegisterRequest account, _) = await RegisterConfirmedAsync();
        (RegisterRequest budgetSpent, _) = await RegisterConfirmedAsync();
        await AccountStateFactory.SpendPasswordResetBudgetAsync(host.Services, budgetSpent.Email);

        // Act
        IReadOnlyList<BranchAnswer> answers = await CollectAsync(new()
        {
            ["unknown address"] = PostJsonAsync(host, "auth/forgot-password", new ForgotPasswordRequest(UnknownAddress())),
            ["real account"] = PostJsonAsync(host, "auth/forgot-password", new ForgotPasswordRequest(account.Email)),
            ["real account, budget spent"] =
                PostJsonAsync(host, "auth/forgot-password", new ForgotPasswordRequest(budgetSpent.Email))
        });

        // Assert
        answers.ShouldAllBe(answer => answer.Elapsed >= ResponseTimeFloors.AccountLookup);
        answers.ShouldAllBe(answer => answer.Status == HttpStatusCode.OK);
    }

    [Fact]
    public async Task ResetPasswordPage_ShouldWaitForTheFloor_OnEveryBranchReachableWithoutTheToken()
    {
        // Arrange
        WebApplicationFactory<Program> host = await Factory.GetResponseTimeFloorHostAsync();
        (RegisterRequest account, _) = await RegisterConfirmedAsync();
        (RegisterRequest deletionScheduled, _) = await RegisterConfirmedAsync();
        await AccountStateFactory.ScheduleDeletionAsync(Factory.Services, deletionScheduled.Email);

        // Act
        IReadOnlyList<BranchAnswer> answers = await CollectAsync(new()
        {
            ["unknown address"] = PostResetPageAsync(host, UnknownAddress()),
            ["real account, wrong token"] = PostResetPageAsync(host, account.Email),
            ["deletion scheduled"] = PostResetPageAsync(host, deletionScheduled.Email)
        });

        // Assert
        answers.ShouldAllBe(answer => answer.Elapsed >= ResponseTimeFloors.AccountLookup);
        answers.ShouldAllBe(answer => answer.Status == HttpStatusCode.OK);
    }

    [Fact]
    public async Task ResetPasswordEndpoint_ShouldWaitForTheFloor_OnEveryBranchReachableWithoutTheToken()
    {
        // Arrange
        WebApplicationFactory<Program> host = await Factory.GetResponseTimeFloorHostAsync();
        (RegisterRequest account, _) = await RegisterConfirmedAsync();
        (RegisterRequest deletionScheduled, _) = await RegisterConfirmedAsync();
        await AccountStateFactory.ScheduleDeletionAsync(Factory.Services, deletionScheduled.Email);

        // Act
        IReadOnlyList<BranchAnswer> answers = await CollectAsync(new()
        {
            ["unknown address"] = PostJsonAsync(host, "auth/reset-password", ResetRequest(UnknownAddress())),
            ["real account, wrong token"] = PostJsonAsync(host, "auth/reset-password", ResetRequest(account.Email)),
            ["deletion scheduled"] = PostJsonAsync(host, "auth/reset-password", ResetRequest(deletionScheduled.Email))
        });

        // Assert
        answers.ShouldAllBe(answer => answer.Elapsed >= ResponseTimeFloors.AccountLookup);
        answers.ShouldAllBe(answer => answer.Status == HttpStatusCode.BadRequest);
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
        answers.ShouldAllBe(answer => answer.Status == HttpStatusCode.OK);
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
        answers.ShouldAllBe(answer => answer.Status == HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CancelDeletionEndpoint_ShouldWaitForTheFloor_OnEveryBranchReachableWithoutTheToken()
    {
        // Arrange
        WebApplicationFactory<Program> host = await Factory.GetResponseTimeFloorHostAsync();
        (RegisterRequest account, _) = await RegisterConfirmedAsync();
        (RegisterRequest deletionScheduled, _) = await RegisterConfirmedAsync();
        await AccountStateFactory.ScheduleDeletionAsync(Factory.Services, deletionScheduled.Email);

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
        answers.ShouldAllBe(answer => answer.Status == HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// The resend page calls the same handler, so the endpoint covers both. The unconfirmed branch sends a
    /// mail, but the spy sender returns at once, so only the floor can make that answer wait.
    /// </summary>
    [Fact]
    public async Task ResendConfirmationEndpoint_ShouldWaitForTheLongerFloor_OnEveryBranch()
    {
        // Arrange
        WebApplicationFactory<Program> host = await Factory.GetResponseTimeFloorHostAsync();
        (RegisterRequest unconfirmed, _) = await RegisterUnconfirmedAsync();
        (RegisterRequest confirmed, _) = await RegisterConfirmedAsync();
        (RegisterRequest budgetSpent, _) = await RegisterUnconfirmedAsync();
        await AccountStateFactory.SpendConfirmationResendBudgetAsync(host.Services, budgetSpent.Email);

        // Act
        IReadOnlyList<BranchAnswer> answers = await CollectAsync(new()
        {
            ["unknown address"] = PostJsonAsync(
                host, "auth/resend-email-confirmation", new ResendEmailConfirmationRequest(UnknownAddress())),
            ["unconfirmed, budget spent"] = PostJsonAsync(
                host, "auth/resend-email-confirmation", new ResendEmailConfirmationRequest(budgetSpent.Email)),
            ["unconfirmed"] = PostJsonAsync(
                host, "auth/resend-email-confirmation", new ResendEmailConfirmationRequest(unconfirmed.Email)),
            ["confirmed"] = PostJsonAsync(
                host, "auth/resend-email-confirmation", new ResendEmailConfirmationRequest(confirmed.Email))
        });

        // Assert
        answers.ShouldAllBe(answer => answer.Elapsed >= ResponseTimeFloors.LiveMailSend);
        answers.ShouldAllBe(answer => answer.Status == HttpStatusCode.OK);
    }

    /// <summary>
    /// A free address creates an account and a taken one writes only a notice, so the timing would tell
    /// them apart without the floor (#860). A spent budget answers 429 on both, and waits as well.
    /// </summary>
    [Fact]
    public async Task RegisterEndpoint_ShouldWaitForTheRegistrationFloor_OnEveryBranch()
    {
        // Arrange
        WebApplicationFactory<Program> host = await Factory.GetResponseTimeFloorHostAsync();
        (RegisterRequest taken, _) = await RegisterConfirmedAsync();
        (RegisterRequest owner, _) = await RegisterConfirmedAsync();
        string reserved = UnknownAddress();
        await AccountStateFactory.ReserveAddressAsync(Factory.Services, owner.Email, reserved);
        (RegisterRequest takenBudgetSpent, _) = await RegisterConfirmedAsync();
        AccountStateFactory.SpendRegistrationBudget(host.Services, takenBudgetSpent.Email);
        string freeBudgetSpent = UnknownAddress();
        AccountStateFactory.SpendRegistrationBudget(host.Services, freeBudgetSpent);
        (RegisterRequest secondOwner, _) = await RegisterConfirmedAsync();
        string reservedBudgetSpent = UnknownAddress();
        await AccountStateFactory.ReserveAddressAsync(Factory.Services, secondOwner.Email, reservedBudgetSpent);
        AccountStateFactory.SpendRegistrationBudget(host.Services, reservedBudgetSpent);

        // Act
        IReadOnlyList<BranchAnswer> answers = await CollectAsync(new()
        {
            ["free address"] = PostJsonAsync(host, "auth/register", NewRegistration(UnknownAddress())),
            ["taken address"] = PostJsonAsync(host, "auth/register", NewRegistration(taken.Email)),
            ["reserved address"] = PostJsonAsync(host, "auth/register", NewRegistration(reserved)),
            ["taken address, budget spent"] =
                PostJsonAsync(host, "auth/register", NewRegistration(takenBudgetSpent.Email)),
            ["free address, budget spent"] = PostJsonAsync(host, "auth/register", NewRegistration(freeBudgetSpent)),
            ["reserved address, budget spent"] =
                PostJsonAsync(host, "auth/register", NewRegistration(reservedBudgetSpent))
        });

        // Assert
        answers.ShouldAllBe(answer => answer.Elapsed >= ResponseTimeFloors.Registration);
        answers.Where(answer => !answer.Branch.EndsWith("budget spent", StringComparison.Ordinal))
            .ShouldAllBe(answer => answer.Status == HttpStatusCode.Accepted);
        answers.Where(answer => answer.Branch.EndsWith("budget spent", StringComparison.Ordinal))
            .ShouldAllBe(answer => answer.Status == HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task RegisterPage_ShouldWaitForTheRegistrationFloor_WhetherOrNotTheAddressHasAnAccount()
    {
        // Arrange
        WebApplicationFactory<Program> host = await Factory.GetResponseTimeFloorHostAsync();
        (RegisterRequest taken, _) = await RegisterConfirmedAsync();
        (RegisterRequest owner, _) = await RegisterConfirmedAsync();
        string reserved = UnknownAddress();
        await AccountStateFactory.ReserveAddressAsync(Factory.Services, owner.Email, reserved);

        // Act
        IReadOnlyList<BranchAnswer> answers = await CollectAsync(new()
        {
            ["free address"] = PostRegisterPageAsync(host, NewRegistration(UnknownAddress())),
            ["taken address"] = PostRegisterPageAsync(host, NewRegistration(taken.Email)),
            ["reserved address"] = PostRegisterPageAsync(host, NewRegistration(reserved))
        });

        // Assert
        answers.ShouldAllBe(answer => answer.Elapsed >= ResponseTimeFloors.Registration);
        answers.ShouldAllBe(answer => answer.Status == HttpStatusCode.OK);
    }

    private static async Task<IReadOnlyList<BranchAnswer>> CollectAsync(Dictionary<string, Task<Answer>> pending)
    {
        // Wait for every request first, so none is still running when the next test cleans the database.
        await Task.WhenAll(pending.Values);

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

    private static async Task<Answer> PostRegisterPageAsync(WebApplicationFactory<Program> host, RegisterRequest request) =>
        await PostPageAsync(host, "/Account/Register", new Dictionary<string, string>
        {
            ["Username"] = request.Username,
            ["Email"] = request.Email,
            ["Password"] = request.Password,
            ["ConfirmPassword"] = request.Password,
            ["AcceptedPrivacyPolicy"] = "true",
            ["AcceptedTermsOfService"] = "true"
        });

    private static ResetPasswordRequest ResetRequest(string email) => new(email, WrongToken, "NewPass99!");

    private RegisterRequest NewRegistration(string email) =>
        UserFactory.GenerateRandomRegisterRequest(Faker) with { Email = email };

    private string UnknownAddress() => "nobody-" + Faker.Random.AlphaNumeric(8) + "@example.com";

    private async Task<(RegisterRequest Request, IdentityId Id)> RegisterConfirmedAsync() =>
        await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy);

    private async Task<(RegisterRequest Request, IdentityId Id)> RegisterUnconfirmedAsync() =>
        await UserFactory.RegisterRandomUserUnconfirmedAsync(ApiClient, Faker, AccountConfirmationEmailSpy);

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
