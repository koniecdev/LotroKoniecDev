using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using LotroKoniecDev.AuthSystem.API.Services.RateLimiting;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.AuthSystem.Persistence.Identity;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

/// <summary>
/// The reservation that keeps an armed undo target out of everyone else's hands (#684). Taking the
/// address a change just freed used to kill the owner's "to nie ja" link for good, and it cost one
/// anonymous POST.
/// </summary>
public sealed partial class EmailChangeRevertReservationTests : EndpointsTestBase
{
    private const string Password = "TestPass1!";

    public EmailChangeRevertReservationTests(AuthSystemApiFactory appFactory) : base(appFactory) { }

    [Fact]
    public async Task Register_ShouldBeRefused_WhileTheFreedAddressIsAnArmedRevertTarget()
    {
        (RegisterRequest user, _, _) = await CompleteChangeAsync();

        HttpResponseMessage response = await RegisterOnAsync(user.Email);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadAsStringAsync()).ShouldContain("Auth.UserAlreadyExistsByEmail");
    }

    [Fact]
    public async Task RevertPage_Post_ShouldRestoreTheAddress_AfterASquatAttempt()
    {
        // The whole attack, end to end: the address moves, somebody tries to occupy the one it came
        // from, and the owner's undo still works.
        (RegisterRequest user, string newEmail, Guid userId) = await CompleteChangeAsync();
        string revertToken = EmailChangeEmailSpy.LastRevertToken!;

        (await RegisterOnAsync(user.Email)).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        HttpResponseMessage response = await PostToPageAsync(
            "/Account/RevertEmailChange",
            RevertUrl(userId, user.Email, newEmail, revertToken),
            RevertForm(userId, user.Email, newEmail, revertToken));

        // The test client follows redirects, so the landing URL is what separates a real revert from
        // the failure rendering, which also answers 200.
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.RequestMessage!.RequestUri!.ToString().ShouldContain("/Account/ResetPassword");

        ApplicationUser restored = await LoadUserByIdAsync(userId);
        restored.Email.ShouldBe(user.Email);
        restored.PasswordHash.ShouldBeNull();
    }

    [Fact]
    public async Task RevertPage_Post_ShouldReleaseTheReservation()
    {
        (RegisterRequest user, string newEmail, Guid userId) = await CompleteChangeAsync();
        string revertToken = EmailChangeEmailSpy.LastRevertToken!;

        await PostToPageAsync(
            "/Account/RevertEmailChange",
            RevertUrl(userId, user.Email, newEmail, revertToken),
            RevertForm(userId, user.Email, newEmail, revertToken));

        // Nothing is armed any more, so the address is guarded by ordinary uniqueness again.
        ApplicationUser restored = await LoadUserByIdAsync(userId);
        restored.EmailChangeRevertTo.ShouldBeNull();
        restored.NormalizedEmailChangeRevertTo.ShouldBeNull();
        restored.EmailChangeRevertArmedAt.ShouldBeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Register_ShouldBeRefused_WhateverCaseTheSquatterTypesTheAddressIn(bool upperCase)
    {
        // Both sides go through Identity's key normalizer, so a re-spelling is the same address. A
        // lookup that quietly degraded to a raw comparison would pass every other test in this file.
        (RegisterRequest user, _, _) = await CompleteChangeAsync();
        string respelled = upperCase ? user.Email.ToUpperInvariant() : MixCase(user.Email);

        HttpResponseMessage response = await RegisterOnAsync(respelled);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadAsStringAsync()).ShouldContain("Auth.UserAlreadyExistsByEmail");
    }

    [Fact]
    public async Task Register_ShouldSucceed_WhenTheAddressOnlyLooksLikeTheArmedOne()
    {
        // Identity upper-cases and nothing else, so a plus tag is a different address. Pinned so
        // nobody later "improves" the normalizer into treating the two as one. Only the send budgets
        // fold the tag (ADR-0057); the address and its reservation never do.
        (RegisterRequest user, _, _) = await CompleteChangeAsync();
        string tagged = user.Email.Replace("@", "+tag@", StringComparison.Ordinal);

        HttpResponseMessage response = await RegisterOnAsync(tagged);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Register_ShouldSucceed_OnceTheArmingHasExpired()
    {
        // The reservation dies with the link it protects. An address is never blocked forever.
        (RegisterRequest user, _, Guid userId) = await CompleteChangeAsync();
        await BackdateArmingAsync(userId, TimeSpan.FromDays(15));

        HttpResponseMessage response = await RegisterOnAsync(user.Email);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Theory]
    [InlineData(13, false)]
    [InlineData(14, true)]
    [InlineData(20, true)]
    public async Task Register_ShouldFollowTheTokenLifespan_AtTheBoundary(int armedDaysAgo, bool allowed)
    {
        // The window is the revert token's own 14 days, measured from the arming. Day 14 is already
        // out: the reservation may not outlive the link it protects.
        (RegisterRequest user, _, Guid userId) = await CompleteChangeAsync();
        await BackdateArmingAsync(userId, TimeSpan.FromDays(armedDaysAgo));

        HttpResponseMessage response = await RegisterOnAsync(user.Email);

        response.StatusCode.ShouldBe(allowed ? HttpStatusCode.Created : HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Register_ShouldSucceed_WhenTheArmingHasNoTimestamp()
    {
        // A row armed before this shipped carries no timestamp. It keeps the behaviour it was written
        // under rather than blocking its address for ever on a value nobody can date.
        (RegisterRequest user, _, Guid userId) = await CompleteChangeAsync();
        await ClearArmingTimestampAsync(userId);

        HttpResponseMessage response = await RegisterOnAsync(user.Email);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task RequestEmailChange_ShouldReturnProblem_WhenTheAddressIsAnotherAccountsRevertTarget()
    {
        (RegisterRequest owner, _, _) = await CompleteChangeAsync();
        (RegisterRequest other, _) = await UserFactory.RegisterRandomUserWithRequestAsync(
            ApiClient, Faker, AccountConfirmationEmailSpy, Password);
        string accessToken = await GetAccessTokenAsync(other.Email, Password);

        HttpResponseMessage response = await RequestChangeAsync(accessToken, owner.Email);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadAsStringAsync()).ShouldContain("Auth.UserAlreadyExistsByEmail");
    }

    [Fact]
    public async Task RequestEmailChange_ShouldBeAccepted_WhenTheAddressIsTheCallersOwnRevertTarget()
    {
        // Going back to where the chain started is the move this reservation exists to protect. It
        // must never be the move it blocks.
        (RegisterRequest user, string newEmail, _) = await CompleteChangeAsync();
        string accessToken = await GetAccessTokenAsync(newEmail, Password);

        HttpResponseMessage response = await RequestChangeAsync(accessToken, user.Email);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task RequestEmailChange_ShouldNotLetAStrangerSpendTheSendBudgetOfTheCallersOwnRevertTarget()
    {
        // The send budget for a new address is taken after the reservation check (ADR-0055). Taken
        // earlier, a stranger's refused requests would spend it, and the owner could not go back.
        (RegisterRequest owner, string newEmail, _) = await CompleteChangeAsync();
        (RegisterRequest stranger, _) = await UserFactory.RegisterRandomUserWithRequestAsync(
            ApiClient, Faker, AccountConfirmationEmailSpy, Password);
        string strangerToken = await GetAccessTokenAsync(stranger.Email, Password);

        for (int i = 0; i < AccountBudgets.EmailChangeRecipientPermitLimit + 1; i++)
        {
            HttpResponseMessage refused = await RequestChangeAsync(strangerToken, owner.Email);
            refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        }

        string ownerToken = await GetAccessTokenAsync(newEmail, Password);

        HttpResponseMessage response = await RequestChangeAsync(ownerToken, owner.Email);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ConfirmPage_Post_ShouldRefuse_WhenTheAddressIsAnotherAccountsRevertTarget()
    {
        // Registration is not the only door onto a free address. A second account holding a confirm
        // link minted before the arming must not walk through this one either.
        (RegisterRequest owner, _, _) = await CompleteChangeAsync();
        (RegisterRequest other, _) = await UserFactory.RegisterRandomUserWithRequestAsync(
            ApiClient, Faker, AccountConfirmationEmailSpy, Password);
        Guid otherUserId = await UserIdOfAsync(other.Email);

        string confirmToken = await IssueConfirmTokenAsync(otherUserId, owner.Email);

        HttpResponseMessage response = await PostToPageAsync(
            "/Account/ConfirmEmailChange",
            ConfirmUrl(otherUserId, owner.Email, confirmToken),
            new Dictionary<string, string>
            {
                ["UserId"] = otherUserId.ToString(),
                ["Email"] = owner.Email,
                ["Token"] = confirmToken
            });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await LoadUserByIdAsync(otherUserId)).Email.ShouldBe(other.Email);
    }

    [Fact]
    public async Task ConfirmPage_Post_ShouldLetTheAccountBackOntoItsOwnArmedAddress()
    {
        // The recovery path this whole ticket protects. Exclude the caller's own row by mistake and
        // the account could never go home again.
        (RegisterRequest user, string newEmail, Guid userId) = await CompleteChangeAsync();

        await ChangeAndConfirmAsync(userId, newEmail, user.Email);

        (await LoadUserByIdAsync(userId)).Email.ShouldBe(user.Email);
    }

    [Fact]
    public async Task ConfirmPage_Post_ShouldSettleTheChain_WhenTheAccountComesBackToTheArmedAddress()
    {
        // Coming home by changing back is as final as clicking the undo, so the row is disarmed. Left
        // armed, its timestamp would stay frozen at the first change while a later change still minted
        // a fresh 14-day link — a link outliving the reservation that keeps its address free (#684).
        (RegisterRequest user, string newEmail, Guid userId) = await CompleteChangeAsync();

        await ChangeAndConfirmAsync(userId, newEmail, user.Email);

        ApplicationUser settled = await LoadUserByIdAsync(userId);
        settled.EmailChangeRevertTo.ShouldBeNull();
        settled.NormalizedEmailChangeRevertTo.ShouldBeNull();
        settled.EmailChangeRevertArmedAt.ShouldBeNull();
    }

    [Fact]
    public async Task ConfirmPage_Post_ShouldReArmWithAFreshWindow_WhenTheAccountLeavesAgainAfterComingBack()
    {
        // The chain restarts from here, so the next change arms afresh and its reservation covers the
        // link it hands out. Before #684 this branch re-used the very first timestamp.
        (RegisterRequest user, string newEmail, Guid userId) = await CompleteChangeAsync();
        DateTimeOffset firstArming = (await LoadUserByIdAsync(userId)).EmailChangeRevertArmedAt!.Value;

        await ChangeAndConfirmAsync(userId, newEmail, user.Email);

        // The row is settled by now, so this backdates a leftover timestamp rather than a live
        // arming. It stands in for the calendar: it is what the old code would have re-used, so the
        // asserts below fail against it and pass against a fresh window.
        await BackdateArmingAsync(userId, TimeSpan.FromDays(20));

        string thirdEmail = Faker.Internet.Email();
        await ChangeAndConfirmAsync(userId, user.Email, thirdEmail);

        ApplicationUser reArmed = await LoadUserByIdAsync(userId);
        reArmed.EmailChangeRevertTo.ShouldBe(user.Email);
        reArmed.EmailChangeRevertArmedAt!.Value.ShouldBeGreaterThan(firstArming);

        // And the address the fresh link points at is reserved again.
        (await RegisterOnAsync(user.Email)).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task ConfirmPage_Post_ShouldNotRefreshTheArming_WhenTheChainMovesAgain()
    {
        // A later change may not re-aim the undo target, and it may not extend its reservation
        // either: both would hand the attacker a longer hold on the owner's address.
        (_, string firstNewEmail, Guid userId) = await CompleteChangeAsync();
        DateTimeOffset? armedAtAfterFirstChange = (await LoadUserByIdAsync(userId)).EmailChangeRevertArmedAt;
        armedAtAfterFirstChange.ShouldNotBeNull();

        await ChangeAndConfirmAsync(userId, firstNewEmail, Faker.Internet.Email());

        (await LoadUserByIdAsync(userId)).EmailChangeRevertArmedAt.ShouldBe(armedAtAfterFirstChange);
    }

    /// <summary>
    /// Moves an account from one address to another the ordinary way: request, then confirm. The spy
    /// is reset first, or the wait below returns the previous change's token straight away and the
    /// confirm quietly does nothing — the page answers 200 either way.
    /// </summary>
    private async Task ChangeAndConfirmAsync(Guid userId, string currentEmail, string targetEmail)
    {
        string accessToken = await GetAccessTokenAsync(currentEmail, Password);
        EmailChangeEmailSpy.Reset();

        (await RequestChangeAsync(accessToken, targetEmail)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await EmailChangeEmailSpy.WaitForVerificationCaptureAsync();

        await ConfirmAsync(userId, targetEmail, EmailChangeEmailSpy.LastVerificationToken!);

        (await LoadUserByIdAsync(userId)).Email.ShouldBe(targetEmail);
    }

    private static string MixCase(string email) =>
        string.Concat(email.Select((character, index) =>
            index % 2 == 0 ? char.ToUpperInvariant(character) : char.ToLowerInvariant(character)));

    private async Task<(RegisterRequest User, string NewEmail, Guid UserId)> CompleteChangeAsync()
    {
        (RegisterRequest user, _) = await UserFactory.RegisterRandomUserWithRequestAsync(
            ApiClient, Faker, AccountConfirmationEmailSpy, Password);

        string accessToken = await GetAccessTokenAsync(user.Email, Password);
        string newEmail = Faker.Internet.Email();

        (await RequestChangeAsync(accessToken, newEmail)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await EmailChangeEmailSpy.WaitForVerificationCaptureAsync();

        Guid userId = await UserIdOfAsync(user.Email);
        await ConfirmAsync(userId, newEmail, EmailChangeEmailSpy.LastVerificationToken!);
        await EmailChangeEmailSpy.WaitForRevertOfferCaptureAsync();

        return (user, newEmail, userId);
    }

    private async Task ConfirmAsync(Guid userId, string newEmail, string token)
    {
        HttpResponseMessage response = await PostToPageAsync(
            "/Account/ConfirmEmailChange",
            ConfirmUrl(userId, newEmail, token),
            new Dictionary<string, string>
            {
                ["UserId"] = userId.ToString(),
                ["Email"] = newEmail,
                ["Token"] = token
            });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private async Task<HttpResponseMessage> RegisterOnAsync(string email) =>
        await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/register", UriKind.Relative),
            new RegisterRequest(
                Faker.Random.AlphaNumeric(16),
                email,
                Password,
                AcceptedPrivacyPolicy: true,
                AcceptedDataProcessingConsent: true,
                AcceptedTermsOfService: true));

    private async Task<HttpResponseMessage> RequestChangeAsync(string accessToken, string newEmail)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "auth/account/change-email");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(new ChangeEmailRequest(newEmail, Password));

        return await ApiClient.Http.SendAsync(request);
    }

    /// <summary>
    /// Mints a confirm token the ordinary way, but for an account the request leg would already have
    /// refused. It is the only way to reach the confirm leg's own guard, which has to hold on its own
    /// because a token minted before the arming stays valid after it.
    /// </summary>
    private async Task<string> IssueConfirmTokenAsync(Guid userId, string newEmail)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        UserManager<ApplicationUser> userManager =
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        ApplicationUser user = (await userManager.FindByIdAsync(userId.ToString()))!;

        return await userManager.GenerateUserTokenAsync(
            user,
            EmailChangeTokenProvider.ProviderName,
            EmailChangeTokenProvider.PurposeFor(newEmail));
    }

    private async Task BackdateArmingAsync(Guid userId, TimeSpan age)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        ApplicationUser user = await db.Users.SingleAsync(row => row.Id == userId);
        user.EmailChangeRevertArmedAt = DateTimeOffset.UtcNow - age;
        await db.SaveChangesAsync();
    }

    private async Task ClearArmingTimestampAsync(Guid userId)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        ApplicationUser user = await db.Users.SingleAsync(row => row.Id == userId);
        user.EmailChangeRevertArmedAt = null;
        await db.SaveChangesAsync();
    }

    private async Task<Guid> UserIdOfAsync(string email)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        return await db.Users
            .AsNoTracking()
            .Where(user => user.NormalizedEmail == email.ToUpperInvariant())
            .Select(user => user.Id)
            .SingleAsync();
    }

    private async Task<ApplicationUser> LoadUserByIdAsync(Guid userId)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        return await db.Users.AsNoTracking().SingleAsync(user => user.Id == userId);
    }

    private static Dictionary<string, string> RevertForm(Guid userId, string from, string to, string token) =>
        new()
        {
            ["UserId"] = userId.ToString(),
            ["From"] = from,
            ["To"] = to,
            ["Token"] = token
        };

    private static string ConfirmUrl(Guid userId, string newEmail, string token) =>
        $"/Account/ConfirmEmailChange?userId={userId}&email={Uri.EscapeDataString(newEmail)}"
        + $"&token={Uri.EscapeDataString(token)}";

    private static string RevertUrl(Guid userId, string from, string to, string token) =>
        $"/Account/RevertEmailChange?userId={userId}&from={Uri.EscapeDataString(from)}"
        + $"&to={Uri.EscapeDataString(to)}&token={Uri.EscapeDataString(token)}";

    private async Task<HttpResponseMessage> PostToPageAsync(
        string pagePath, string getUrl, Dictionary<string, string> formFields)
    {
        HttpResponseMessage pageResponse = await ApiClient.Http.GetAsync(new Uri(getUrl, UriKind.Relative));
        pageResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        string html = await pageResponse.Content.ReadAsStringAsync();
        Match match = AntiForgeryTokenRegex().Match(html);
        if (match.Success)
        {
            formFields["__RequestVerificationToken"] = match.Groups[1].Value;
        }

        using FormUrlEncodedContent content = new(formFields);
        using HttpRequestMessage request = new(HttpMethod.Post, pagePath) { Content = content };

        if (pageResponse.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? cookies))
        {
            foreach (string cookie in cookies)
            {
                request.Headers.Add("Cookie", cookie.Split(';')[0]);
            }
        }

        return await ApiClient.Http.SendAsync(request);
    }

    [GeneratedRegex("""name="__RequestVerificationToken".*?value="([^"]+)""")]
    private static partial Regex AntiForgeryTokenRegex();
}
