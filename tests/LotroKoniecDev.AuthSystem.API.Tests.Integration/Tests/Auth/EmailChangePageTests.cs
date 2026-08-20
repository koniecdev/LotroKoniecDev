using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

/// <summary>
/// The two page legs of the e-mail change: the confirmation the new mailbox follows, and the undo the
/// old mailbox holds for 14 days (ADR-0048).
/// </summary>
public sealed partial class EmailChangePageTests : EndpointsTestBase
{
    private const string Password = "TestPass1!";

    public EmailChangePageTests(AuthSystemApiFactory appFactory) : base(appFactory) { }

    [Fact]
    public async Task ConfirmPage_Get_ShouldChangeNothing()
    {
        // The mail-scanner guard. Corporate mail security opens every link it sees, so a GET that
        // applied the change would move addresses nobody clicked on.
        (RegisterRequest user, string newEmail, string token) = await RequestChangeAsync();
        Guid userId = await UserIdOfAsync(user.Email);

        HttpResponseMessage response = await ApiClient.Http.GetAsync(
            new Uri(ConfirmUrl(userId, newEmail, token), UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await LoadUserByIdAsync(userId)).Email.ShouldBe(user.Email);
    }

    [Fact]
    public async Task ConfirmPage_Post_ShouldMoveTheAddressAndKeepItConfirmed()
    {
        (RegisterRequest user, string newEmail, string token) = await RequestChangeAsync();
        Guid userId = await UserIdOfAsync(user.Email);

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

        ApplicationUser moved = await LoadUserByIdAsync(userId);
        moved.Email.ShouldBe(newEmail);

        // RequireConfirmedEmail is on, so an address that landed unconfirmed would lock the user out
        // of their own account.
        moved.EmailConfirmed.ShouldBeTrue();
    }

    [Fact]
    public async Task ConfirmPage_Post_ShouldMakeTheNewAddressTheLogin()
    {
        (RegisterRequest user, string newEmail, string token) = await RequestChangeAsync();
        Guid userId = await UserIdOfAsync(user.Email);
        await ConfirmAsync(userId, newEmail, token);

        string accessToken = await GetAccessTokenAsync(newEmail, Password);

        accessToken.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ConfirmPage_PostTwice_ShouldRefuseTheSecondTime()
    {
        (RegisterRequest user, string newEmail, string token) = await RequestChangeAsync();
        Guid userId = await UserIdOfAsync(user.Email);
        await ConfirmAsync(userId, newEmail, token);

        HttpResponseMessage replay = await PostToPageAsync(
            "/Account/ConfirmEmailChange",
            ConfirmUrl(userId, newEmail, token),
            new Dictionary<string, string>
            {
                ["UserId"] = userId.ToString(),
                ["Email"] = newEmail,
                ["Token"] = token
            });

        (await replay.Content.ReadAsStringAsync()).ShouldContain("nieprawidłowy");
        (await LoadUserByIdAsync(userId)).Email.ShouldBe(newEmail);
    }

    [Fact]
    public async Task ConfirmPage_PostWithATamperedAddress_ShouldChangeNothing()
    {
        // The address is baked into the token's purpose, so editing it in the link has to fail.
        (RegisterRequest user, string newEmail, string token) = await RequestChangeAsync();
        Guid userId = await UserIdOfAsync(user.Email);

        HttpResponseMessage response = await PostToPageAsync(
            "/Account/ConfirmEmailChange",
            ConfirmUrl(userId, newEmail, token),
            new Dictionary<string, string>
            {
                ["UserId"] = userId.ToString(),
                ["Email"] = "attacker@mordor.example",
                ["Token"] = token
            });

        (await response.Content.ReadAsStringAsync()).ShouldContain("nieprawidłowy");
        (await LoadUserByIdAsync(userId)).Email.ShouldBe(user.Email);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("Twoje konto zostało przejęte")]
    public async Task ConfirmPage_GetWithAMalformedUserId_RendersTheErrorStateNotACrash(string userIdInput)
    {
        (RegisterRequest user, string newEmail, string token) = await RequestChangeAsync();

        HttpResponseMessage response = await ApiClient.Http.GetAsync(new Uri(
            $"/Account/ConfirmEmailChange?userId={Uri.EscapeDataString(userIdInput)}"
            + $"&email={Uri.EscapeDataString(newEmail)}&token={Uri.EscapeDataString(token)}",
            UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldContain("nieprawidłowy");
        (await LoadUserByIdAsync(await UserIdOfAsync(user.Email))).Email.ShouldBe(user.Email);
    }

    [Fact]
    public async Task ConfirmPage_GetWithATextInsteadOfAnAddress_DoesNotPrintItOnThePage()
    {
        // The page is on the auth origin and carries a real button, so it must not become a place to
        // put a sentence of somebody else's choosing.
        (RegisterRequest user, _, string token) = await RequestChangeAsync();
        Guid userId = await UserIdOfAsync(user.Email);
        const string injected = "Twoje konto zostalo przejete - zadzwon pod numer 500600700";

        HttpResponseMessage response = await ApiClient.Http.GetAsync(new Uri(
            $"/Account/ConfirmEmailChange?userId={userId}&email={Uri.EscapeDataString(injected)}"
            + $"&token={Uri.EscapeDataString(token)}",
            UriKind.Relative));

        string html = await response.Content.ReadAsStringAsync();
        html.ShouldNotContain(injected);
        html.ShouldContain("nieprawidłowy");
    }

    [Fact]
    public async Task RevertPage_Get_ShouldChangeNothing()
    {
        // The most important instance of the guard: this link arrives in a mailbox the user actually
        // reads, so a GET that reverted would undo every legitimate change automatically.
        (RegisterRequest user, string newEmail, Guid userId) = await CompleteChangeAsync();
        string revertToken = EmailChangeEmailSpy.LastRevertToken!;

        HttpResponseMessage response = await ApiClient.Http.GetAsync(
            new Uri(RevertUrl(userId, user.Email, newEmail, revertToken), UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        ApplicationUser untouched = await LoadUserByIdAsync(userId);
        untouched.Email.ShouldBe(newEmail);
        untouched.PasswordHash.ShouldNotBeNull();
    }

    [Fact]
    public async Task RevertPage_Post_ShouldRestoreTheAddressAndClearThePassword()
    {
        (RegisterRequest user, string newEmail, Guid userId) = await CompleteChangeAsync();
        string revertToken = EmailChangeEmailSpy.LastRevertToken!;

        HttpResponseMessage response = await PostToPageAsync(
            "/Account/RevertEmailChange",
            RevertUrl(userId, user.Email, newEmail, revertToken),
            RevertForm(userId, user.Email, newEmail, revertToken));

        // The password is gone, so the page has to hand the visitor straight to the reset flow. The
        // test client follows redirects, so the landing URL is what proves it — accepting a bare 200
        // would also accept the failure rendering.
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.RequestMessage!.RequestUri!.ToString().ShouldContain("/Account/ResetPassword");

        ApplicationUser restored = await LoadUserByIdAsync(userId);
        restored.Email.ShouldBe(user.Email);
        restored.EmailConfirmed.ShouldBeTrue();
        restored.PasswordHash.ShouldBeNull();
    }

    [Fact]
    public async Task RevertPage_Post_ShouldStillWorkAfterThePasswordWasChanged()
    {
        // This is ADR-0048's whole reason for a hand-written token provider. Whoever took the account
        // over changes the password next, which rotates the security stamp — and every other token in
        // this system dies with that rotation. This one must not.
        (RegisterRequest user, string newEmail, Guid userId) = await CompleteChangeAsync();
        string revertToken = EmailChangeEmailSpy.LastRevertToken!;

        const string attackerPassword = "Attacker99!";
        string accessToken = await GetAccessTokenAsync(newEmail, Password);
        using HttpRequestMessage changePassword = new(HttpMethod.Post, "auth/change-password");
        changePassword.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        changePassword.Content = JsonContent.Create(
            new Contracts.Features.Auth.Password.ChangePasswordRequest(Password, attackerPassword));
        (await ApiClient.Http.SendAsync(changePassword)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await PostToPageAsync(
            "/Account/RevertEmailChange",
            RevertUrl(userId, user.Email, newEmail, revertToken),
            RevertForm(userId, user.Email, newEmail, revertToken));

        ApplicationUser restored = await LoadUserByIdAsync(userId);
        restored.Email.ShouldBe(user.Email);
        restored.PasswordHash.ShouldBeNull();
    }

    [Fact]
    public async Task RevertPage_Post_ShouldStillWorkAfterTheAddressWasChangedAgain()
    {
        // The takeover an earlier draft of this guard let through: whoever holds the password moves the
        // account A -> B, confirms from B, then immediately B -> C. If the revert insisted the account
        // still sat on B, the owner's A -> B link would match nothing, and the fresh revert offer for
        // B -> C would be posted to B, which the attacker owns. The owner has to be able to pull the
        // account back from wherever it has been dragged.
        (RegisterRequest user, string firstNewEmail, Guid userId) = await CompleteChangeAsync();
        string revertToken = EmailChangeEmailSpy.LastRevertToken!;

        string secondNewEmail = Faker.Internet.Email();
        EmailChangeEmailSpy.Reset();
        string accessToken = await GetAccessTokenAsync(firstNewEmail, Password);
        using HttpRequestMessage secondRequest = new(HttpMethod.Post, "auth/account/change-email");
        secondRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        secondRequest.Content = JsonContent.Create(new ChangeEmailRequest(secondNewEmail, Password));
        (await ApiClient.Http.SendAsync(secondRequest)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await EmailChangeEmailSpy.WaitForVerificationCaptureAsync();
        await ConfirmAsync(userId, secondNewEmail, EmailChangeEmailSpy.LastVerificationToken!);
        (await LoadUserByIdAsync(userId)).Email.ShouldBe(secondNewEmail);

        await PostToPageAsync(
            "/Account/RevertEmailChange",
            RevertUrl(userId, user.Email, firstNewEmail, revertToken),
            RevertForm(userId, user.Email, firstNewEmail, revertToken));

        ApplicationUser restored = await LoadUserByIdAsync(userId);
        restored.Email.ShouldBe(user.Email);
        restored.PasswordHash.ShouldBeNull();
    }

    [Fact]
    public async Task RevertPage_Post_ShouldNotLetAnOlderRevertTokenRepossessTheAccount()
    {
        // The counter-takeover. The attacker chains A -> B -> C, so the revert offer for B -> C lands
        // in B, which is theirs. The owner reverts with their A -> B link and resets the password. If
        // the attacker's older token still works, they fire it afterwards and take the account back
        // for good, with no e-mail warning anybody.
        (RegisterRequest user, string attackerFirstEmail, Guid userId) = await CompleteChangeAsync();
        string ownerRevertToken = EmailChangeEmailSpy.LastRevertToken!;

        string attackerSecondEmail = Faker.Internet.Email();
        EmailChangeEmailSpy.Reset();
        string accessToken = await GetAccessTokenAsync(attackerFirstEmail, Password);
        using HttpRequestMessage secondRequest = new(HttpMethod.Post, "auth/account/change-email");
        secondRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        secondRequest.Content = JsonContent.Create(new ChangeEmailRequest(attackerSecondEmail, Password));
        (await ApiClient.Http.SendAsync(secondRequest)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await EmailChangeEmailSpy.WaitForVerificationCaptureAsync();
        await ConfirmAsync(userId, attackerSecondEmail, EmailChangeEmailSpy.LastVerificationToken!);
        await EmailChangeEmailSpy.WaitForRevertOfferCaptureAsync();
        string attackerRevertToken = EmailChangeEmailSpy.LastRevertToken!;

        // The owner takes the account back.
        await PostToPageAsync(
            "/Account/RevertEmailChange",
            RevertUrl(userId, user.Email, attackerFirstEmail, ownerRevertToken),
            RevertForm(userId, user.Email, attackerFirstEmail, ownerRevertToken));
        (await LoadUserByIdAsync(userId)).Email.ShouldBe(user.Email);

        // The attacker fires the token that was mailed to the address they controlled.
        await PostToPageAsync(
            "/Account/RevertEmailChange",
            RevertUrl(userId, attackerFirstEmail, attackerSecondEmail, attackerRevertToken),
            RevertForm(userId, attackerFirstEmail, attackerSecondEmail, attackerRevertToken));

        (await LoadUserByIdAsync(userId)).Email.ShouldBe(user.Email);
    }

    [Fact]
    public async Task RevertPage_Post_ShouldRefuseAndKeepThePassword_WhenThePreviousAddressWasTaken()
    {
        // Nothing to go back to. Clearing the password here would lock the account out of both
        // addresses at once.
        (RegisterRequest user, string newEmail, Guid userId) = await CompleteChangeAsync();
        string revertToken = EmailChangeEmailSpy.LastRevertToken!;

        // The address was freed by the change, so somebody else can claim it. They do not need to
        // confirm it — occupying the row is enough to make the revert impossible.
        await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/register", UriKind.Relative),
            new RegisterRequest(
                Faker.Random.AlphaNumeric(16),
                user.Email,
                Password,
                AcceptedPrivacyPolicy: true,
                AcceptedDataProcessingConsent: true,
                AcceptedTermsOfService: true));

        HttpResponseMessage response = await PostToPageAsync(
            "/Account/RevertEmailChange",
            RevertUrl(userId, user.Email, newEmail, revertToken),
            RevertForm(userId, user.Email, newEmail, revertToken));

        (await response.Content.ReadAsStringAsync()).ShouldContain("nieprawidłowy");

        ApplicationUser untouched = await LoadUserByIdAsync(userId);
        untouched.Email.ShouldBe(newEmail);
        untouched.PasswordHash.ShouldNotBeNull();
    }

    [Fact]
    public async Task RevertPage_PostTwice_ShouldRefuseTheSecondTime()
    {
        // The token carries no security stamp, so what makes it single-use is the check that the
        // account still sits on the address the token was issued against.
        (RegisterRequest user, string newEmail, Guid userId) = await CompleteChangeAsync();
        string revertToken = EmailChangeEmailSpy.LastRevertToken!;

        await PostToPageAsync(
            "/Account/RevertEmailChange",
            RevertUrl(userId, user.Email, newEmail, revertToken),
            RevertForm(userId, user.Email, newEmail, revertToken));

        HttpResponseMessage replay = await PostToPageAsync(
            "/Account/RevertEmailChange",
            RevertUrl(userId, user.Email, newEmail, revertToken),
            RevertForm(userId, user.Email, newEmail, revertToken));

        (await replay.Content.ReadAsStringAsync()).ShouldContain("nieprawidłowy");
        (await LoadUserByIdAsync(userId)).Email.ShouldBe(user.Email);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("../../etc/passwd")]
    public async Task RevertPage_PostWithAMalformedUserId_ShouldRenderTheErrorState(string userIdInput)
    {
        // Identity converts the id string to the key type, so a value that is not a Guid throws deep
        // in the store. A person who followed a link from their inbox has to see the invalid-link
        // page, never a crash.
        (RegisterRequest user, string newEmail, Guid userId) = await CompleteChangeAsync();
        string revertToken = EmailChangeEmailSpy.LastRevertToken!;

        Dictionary<string, string> form = RevertForm(userId, user.Email, newEmail, revertToken);
        form["UserId"] = userIdInput;

        HttpResponseMessage response = await PostToPageAsync(
            "/Account/RevertEmailChange",
            RevertUrl(userId, user.Email, newEmail, revertToken),
            form);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldContain("nieprawidłowy");
    }

    [Fact]
    public async Task RevertPage_PostWithAForeignToken_ShouldChangeNothing()
    {
        (RegisterRequest user, string newEmail, Guid userId) = await CompleteChangeAsync();

        HttpResponseMessage response = await PostToPageAsync(
            "/Account/RevertEmailChange",
            RevertUrl(userId, user.Email, newEmail, "not-a-real-token"),
            RevertForm(userId, user.Email, newEmail, "not-a-real-token"));

        (await response.Content.ReadAsStringAsync()).ShouldContain("nieprawidłowy");

        ApplicationUser untouched = await LoadUserByIdAsync(userId);
        untouched.Email.ShouldBe(newEmail);
        untouched.PasswordHash.ShouldNotBeNull();
    }

    private async Task<(RegisterRequest User, string NewEmail, string Token)> RequestChangeAsync()
    {
        (RegisterRequest user, _) = await UserFactory.RegisterRandomUserWithRequestAsync(
            ApiClient, Faker, AccountConfirmationEmailSpy, Password);

        string accessToken = await GetAccessTokenAsync(user.Email, Password);
        string newEmail = Faker.Internet.Email();

        using HttpRequestMessage request = new(HttpMethod.Post, "auth/account/change-email");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(new ChangeEmailRequest(newEmail, Password));
        (await ApiClient.Http.SendAsync(request)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await EmailChangeEmailSpy.WaitForVerificationCaptureAsync();

        return (user, newEmail, EmailChangeEmailSpy.LastVerificationToken!);
    }

    private async Task<(RegisterRequest User, string NewEmail, Guid UserId)> CompleteChangeAsync()
    {
        (RegisterRequest user, string newEmail, string token) = await RequestChangeAsync();
        Guid userId = await UserIdOfAsync(user.Email);

        await ConfirmAsync(userId, newEmail, token);
        await EmailChangeEmailSpy.WaitForRevertOfferCaptureAsync();

        EmailChangeEmailSpy.LastRevertOfferRecipient.ShouldBe(user.Email);
        EmailChangeEmailSpy.LastNoticeRecipient.ShouldBe(newEmail);

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

    private async Task<Guid> UserIdOfAsync(string email)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        return await db.Set<ApplicationUser>()
            .AsNoTracking()
            .Where(user => user.NormalizedEmail == email.ToUpperInvariant())
            .Select(user => user.Id)
            .SingleAsync();
    }

    private async Task<ApplicationUser> LoadUserByIdAsync(Guid userId)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        return await db.Set<ApplicationUser>().AsNoTracking().SingleAsync(user => user.Id == userId);
    }

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
