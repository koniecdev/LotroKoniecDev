using System.Globalization;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Discovery;
using LotroKoniecDev.AuthSystem.Contracts.Features.Admin;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.AuthSystem.Contracts.Hateoas;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.SharedKernel.Authorization;
using LotroKoniecDev.SharedKernel.Constants;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using OpenIddict.Abstractions;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

/// <summary>
/// The admin's account list and the Reviewer role it gives and takes away (#1488): only the admin may do
/// it, a real change signs the person out, and a repeated call changes nothing.
/// </summary>
public sealed class AccountAdministrationEndpointTests : EndpointsTestBase
{
    private const string Password = "TestPass1!";
    private const string AccountsPath = "auth/admin/accounts";
    private const int OverlappingCalls = 20;

    public enum CallerRole
    {
        Translator,
        TranslatorAndReviewer
    }

    public enum UngrantableAccount
    {
        Admin,
        DeletionScheduled,
        WithoutTranslator
    }

    [Fact]
    public async Task ListAccounts_AsAdmin_ShouldReturnEveryAccountWithItsRoles()
    {
        // Arrange
        string adminToken = await AdminAccessTokenAsync();
        (RegisterRequest translator, IdentityId translatorId) = await RegisterAsync();

        // Act
        AccountListResponse response = await GetAccountsAsync(adminToken);

        // Assert
        response.TotalCount.ShouldBe(2);
        response.Page.ShouldBe(1);
        AccountSummaryResponse account = response.Items.Single(item => item.UserId == translatorId.Value);
        account.Username.ShouldBe(translator.Username);
        account.Email.ShouldBe(translator.Email);
        account.Roles.ShouldBe([AuthConstants.Roles.Translator]);
        account.DeletionScheduledAt.ShouldBeNull();
    }

    [Fact]
    public async Task ListAccounts_ShouldLeaveOutAnErasedAccount()
    {
        // Arrange: erasure keeps only an anonymous row.
        string adminToken = await AdminAccessTokenAsync();
        (_, IdentityId erasedId) = await RegisterAsync();
        await AnonymizeAsync(erasedId.Value);

        // Act
        AccountListResponse response = await GetAccountsAsync(adminToken);

        // Assert
        response.TotalCount.ShouldBe(1);
        response.Items.ShouldNotContain(item => item.UserId == erasedId.Value);
    }

    [Fact]
    public async Task ListAccounts_ShouldListALiveAccountWhoseAddressOnlyLooksErased()
    {
        // Arrange: the marker address alone is no proof of erasure; the account still has a password
        // (ADR-0065).
        string adminToken = await AdminAccessTokenAsync();
        (_, IdentityId liveId) = await RegisterAsync();
        await MoveToTheMarkerDomainAsync(liveId.Value);

        // Act
        AccountListResponse response = await GetAccountsAsync(adminToken);

        // Assert
        response.Items.ShouldContain(item => item.UserId == liveId.Value);
    }

    [Theory]
    [InlineData(CallerRole.Translator)]
    [InlineData(CallerRole.TranslatorAndReviewer)]
    public async Task ListAccounts_WithoutTheAdminRole_ShouldReturn403(CallerRole callerRole)
    {
        // Arrange
        string token = await CallerAccessTokenAsync(callerRole);

        // Act
        using HttpResponseMessage response = await SendAsync(HttpMethod.Get, AccountsPath, token);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ListAccounts_WithoutAToken_ShouldReturn401()
    {
        // Act
        using HttpResponseMessage response = await SendAsync(HttpMethod.Get, AccountsPath, accessToken: null);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ListAccounts_ShouldOfferEachAccountTheRoleChangeItsStateAllows()
    {
        // Arrange
        string adminToken = await AdminAccessTokenAsync();
        (_, IdentityId translatorId) = await RegisterAsync();
        (RegisterRequest reviewer, IdentityId reviewerId) = await RegisterAsync();
        await AccountStateFactory.AddToRoleAsync(Factory.Services, reviewer.Email, AuthConstants.Roles.Reviewer);
        (RegisterRequest leavingReviewer, IdentityId leavingReviewerId) = await RegisterAsync();
        await AccountStateFactory.AddToRoleAsync(Factory.Services, leavingReviewer.Email, AuthConstants.Roles.Reviewer);
        await AccountStateFactory.ScheduleDeletionAsync(Factory.Services, leavingReviewer.Email);
        (RegisterRequest leavingTranslator, IdentityId leavingTranslatorId) = await RegisterAsync();
        await AccountStateFactory.ScheduleDeletionAsync(Factory.Services, leavingTranslator.Email);

        // Act
        AccountListResponse response = await GetAccountsAsync(adminToken);

        // Assert
        RelsOf(response, translatorId).ShouldBe([Rels.GrantReviewer]);
        RelsOf(response, reviewerId).ShouldBe([Rels.RemoveReviewer]);
        RelsOf(response, leavingReviewerId).ShouldBe([Rels.RemoveReviewer]);
        RelsOf(response, leavingTranslatorId).ShouldBeEmpty();
        response.Items.Single(item => item.Roles.Contains(AuthConstants.Roles.Admin)).Links.ShouldBeEmpty();
    }

    [Fact]
    public async Task ListAccounts_WithMoreAccountsThanAPage_ShouldLinkTheNextPageAndBack()
    {
        // Arrange: 50 accounts plus the admin fill more than one page.
        string adminToken = await AdminAccessTokenAsync();
        await CreateAccountsAsync(50);

        // Act
        AccountListResponse firstPage = await GetAccountsAsync(adminToken);
        AccountListResponse secondPage = await GetAccountsAsync(adminToken, firstPage.Links.Single(l => l.Rel == Rels.NextPage).Href);

        // Assert
        firstPage.Items.Count.ShouldBe(50);
        firstPage.Links.ShouldNotContain(l => l.Rel == Rels.PreviousPage);
        secondPage.Page.ShouldBe(2);
        secondPage.Items.Count.ShouldBe(1);
        secondPage.Links.ShouldContain(l => l.Rel == Rels.PreviousPage);
        secondPage.Links.ShouldNotContain(l => l.Rel == Rels.NextPage);
        firstPage.Items.Select(item => item.UserId).Intersect(secondPage.Items.Select(item => item.UserId)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ListAccounts_WithAPageBelowOne_ShouldReturn400(int page)
    {
        // Arrange
        string adminToken = await AdminAccessTokenAsync();

        // Act
        using HttpResponseMessage response = await SendAsync(
            HttpMethod.Get, string.Create(CultureInfo.InvariantCulture, $"{AccountsPath}?page={page}"), adminToken);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(42949674)]
    [InlineData(int.MaxValue)]
    public async Task ListAccounts_WithAPagePastTheEnd_ShouldReturnAnEmptyPage(int page)
    {
        // Arrange: a huge page number must not wrap into a negative database offset.
        string adminToken = await AdminAccessTokenAsync();

        // Act
        AccountListResponse response = await GetAccountsAsync(
            adminToken, string.Create(CultureInfo.InvariantCulture, $"{AccountsPath}?page={page}"));

        // Assert
        response.Page.ShouldBe(page);
        response.Items.ShouldBeEmpty();
        response.TotalCount.ShouldBe(1);
    }

    [Fact]
    public async Task Discovery_AsAdmin_ShouldOfferTheAccountList()
    {
        // Arrange: a translator gets no such link (DiscoveryHateoasTests counts its links).
        string adminToken = await AdminAccessTokenAsync();

        // Act
        using HttpResponseMessage response = await SendAsync(HttpMethod.Get, "", adminToken);

        // Assert
        DiscoveryResponse discovery = (await response.Content.ReadFromJsonAsync<DiscoveryResponse>(ApiClient.JsonOptions))!;
        discovery.Links.ShouldContain(l => l.Rel == Rels.Accounts && l.Method == "GET");
    }

    [Fact]
    public async Task GrantReviewer_AsAdmin_ShouldAddTheRoleNextToTranslatorAndEndTheSessions()
    {
        // Arrange
        string adminToken = await AdminAccessTokenAsync();
        (RegisterRequest translator, IdentityId translatorId) = await RegisterAsync();
        string refreshToken = await GetRefreshTokenAsync(translator.Email, Password);
        string stampBefore = await SecurityStampOfAsync(translatorId.Value);

        // Act
        using HttpResponseMessage response = await SendAsync(HttpMethod.Put, ReviewerPath(translatorId.Value), adminToken);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await RolesOfAsync(translatorId.Value)).ShouldBe(
            [AuthConstants.Roles.Reviewer, AuthConstants.Roles.Translator], ignoreOrder: true);
        (await OpenIddictTokenState.StatusOfAsync(Factory.Services, refreshToken)).ShouldBe(OpenIddictConstants.Statuses.Revoked);
        // A stamp change would break the links already mailed to the account.
        (await SecurityStampOfAsync(translatorId.Value)).ShouldBe(stampBefore);
    }

    [Fact]
    public async Task GrantReviewer_ForAnAccountThatIsAlreadyAReviewer_ShouldChangeNothing()
    {
        // Arrange: the website retries a write that timed out.
        string adminToken = await AdminAccessTokenAsync();
        (RegisterRequest translator, IdentityId translatorId) = await RegisterAsync();
        using HttpResponseMessage firstGrant = await SendAsync(HttpMethod.Put, ReviewerPath(translatorId.Value), adminToken);
        firstGrant.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        string refreshTokenAfterSignIn = await GetRefreshTokenAsync(translator.Email, Password);

        // Act
        using HttpResponseMessage response = await SendAsync(HttpMethod.Put, ReviewerPath(translatorId.Value), adminToken);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await OpenIddictTokenState.StatusOfAsync(Factory.Services, refreshTokenAfterSignIn)).ShouldBe(OpenIddictConstants.Statuses.Valid);
    }

    [Fact]
    public async Task GrantReviewer_ForOverlappingCalls_ShouldAnswerEachWith204AndAddTheRoleOnce()
    {
        // Arrange: the website retries a write that timed out while the first one still runs.
        string adminToken = await AdminAccessTokenAsync();
        (_, IdentityId translatorId) = await RegisterAsync();

        // Act
        HttpStatusCode[] statusCodes = await SendOverlappingAsync(HttpMethod.Put, ReviewerPath(translatorId.Value), adminToken);

        // Assert
        statusCodes.ShouldAllBe(statusCode => statusCode == HttpStatusCode.NoContent);
        (await RolesOfAsync(translatorId.Value)).ShouldBe(
            [AuthConstants.Roles.Reviewer, AuthConstants.Roles.Translator], ignoreOrder: true);
    }

    [Theory]
    [InlineData(UngrantableAccount.Admin)]
    [InlineData(UngrantableAccount.DeletionScheduled)]
    [InlineData(UngrantableAccount.WithoutTranslator)]
    public async Task GrantReviewer_ForAnAccountThatCannotHoldTheRole_ShouldReturn422AndChangeNothing(
        UngrantableAccount state)
    {
        // Arrange
        string adminToken = await AdminAccessTokenAsync();
        (RegisterRequest target, IdentityId targetId) = await RegisterAsync();
        await ApplyAsync(state, target.Email);

        // Act
        using HttpResponseMessage response = await SendAsync(HttpMethod.Put, ReviewerPath(targetId.Value), adminToken);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await RolesOfAsync(targetId.Value)).ShouldNotContain(AuthConstants.Roles.Reviewer);
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task ChangeReviewerRole_ForAnUnknownAccount_ShouldReturn404(string method)
    {
        // Arrange
        string adminToken = await AdminAccessTokenAsync();

        // Act
        using HttpResponseMessage response = await SendAsync(new HttpMethod(method), ReviewerPath(Guid.NewGuid()), adminToken);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("PUT", "GrantReviewerRole.Validation")]
    [InlineData("DELETE", "RemoveReviewerRole.Validation")]
    public async Task ChangeReviewerRole_ForTheEmptyAccountId_ShouldReturn400(string method, string errorCode)
    {
        // Arrange
        string adminToken = await AdminAccessTokenAsync();

        // Act
        using HttpResponseMessage response = await SendAsync(new HttpMethod(method), ReviewerPath(Guid.Empty), adminToken);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain(errorCode);
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task ChangeReviewerRole_WithoutAToken_ShouldReturn401(string method)
    {
        // Act
        using HttpResponseMessage response = await SendAsync(new HttpMethod(method), ReviewerPath(Guid.NewGuid()), accessToken: null);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("PUT", CallerRole.Translator)]
    [InlineData("PUT", CallerRole.TranslatorAndReviewer)]
    [InlineData("DELETE", CallerRole.Translator)]
    [InlineData("DELETE", CallerRole.TranslatorAndReviewer)]
    public async Task ChangeReviewerRole_WithoutTheAdminRole_ShouldReturn403AndChangeNothing(string method, CallerRole callerRole)
    {
        // Arrange
        (RegisterRequest target, IdentityId targetId) = await RegisterAsync();
        await AccountStateFactory.AddToRoleAsync(Factory.Services, target.Email, AuthConstants.Roles.Reviewer);
        string token = await CallerAccessTokenAsync(callerRole);

        // Act
        using HttpResponseMessage response = await SendAsync(new HttpMethod(method), ReviewerPath(targetId.Value), token);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await RolesOfAsync(targetId.Value)).ShouldContain(AuthConstants.Roles.Reviewer);
    }

    [Fact]
    public async Task RemoveReviewer_AsAdmin_ShouldTakeOnlyThatRoleAndEndTheSessions()
    {
        // Arrange
        string adminToken = await AdminAccessTokenAsync();
        (RegisterRequest reviewer, IdentityId reviewerId) = await RegisterAsync();
        await AccountStateFactory.AddToRoleAsync(Factory.Services, reviewer.Email, AuthConstants.Roles.Reviewer);
        string refreshToken = await GetRefreshTokenAsync(reviewer.Email, Password);

        // Act
        using HttpResponseMessage response = await SendAsync(HttpMethod.Delete, ReviewerPath(reviewerId.Value), adminToken);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await RolesOfAsync(reviewerId.Value)).ShouldBe([AuthConstants.Roles.Translator]);
        (await OpenIddictTokenState.StatusOfAsync(Factory.Services, refreshToken)).ShouldBe(OpenIddictConstants.Statuses.Revoked);
    }

    [Fact]
    public async Task RemoveReviewer_ForAnAccountWithoutTheRole_ShouldChangeNothing()
    {
        // Arrange
        string adminToken = await AdminAccessTokenAsync();
        (RegisterRequest translator, IdentityId translatorId) = await RegisterAsync();
        string refreshToken = await GetRefreshTokenAsync(translator.Email, Password);

        // Act
        using HttpResponseMessage response = await SendAsync(HttpMethod.Delete, ReviewerPath(translatorId.Value), adminToken);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await OpenIddictTokenState.StatusOfAsync(Factory.Services, refreshToken)).ShouldBe(OpenIddictConstants.Statuses.Valid);
    }

    [Fact]
    public async Task RemoveReviewer_ForAReviewerWaitingForDeletion_ShouldStillTakeTheRole()
    {
        // Arrange: taking a right away is never refused for the account's state.
        string adminToken = await AdminAccessTokenAsync();
        (RegisterRequest reviewer, IdentityId reviewerId) = await RegisterAsync();
        await AccountStateFactory.AddToRoleAsync(Factory.Services, reviewer.Email, AuthConstants.Roles.Reviewer);
        await AccountStateFactory.ScheduleDeletionAsync(Factory.Services, reviewer.Email);

        // Act
        using HttpResponseMessage response = await SendAsync(HttpMethod.Delete, ReviewerPath(reviewerId.Value), adminToken);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await RolesOfAsync(reviewerId.Value)).ShouldNotContain(AuthConstants.Roles.Reviewer);
    }

    [Fact]
    public async Task RemoveReviewer_ForOverlappingCalls_ShouldAnswerEachWith204AndTakeTheRole()
    {
        // Arrange
        string adminToken = await AdminAccessTokenAsync();
        (RegisterRequest reviewer, IdentityId reviewerId) = await RegisterAsync();
        await AccountStateFactory.AddToRoleAsync(Factory.Services, reviewer.Email, AuthConstants.Roles.Reviewer);

        // Act
        HttpStatusCode[] statusCodes = await SendOverlappingAsync(HttpMethod.Delete, ReviewerPath(reviewerId.Value), adminToken);

        // Assert
        statusCodes.ShouldAllBe(statusCode => statusCode == HttpStatusCode.NoContent);
        (await RolesOfAsync(reviewerId.Value)).ShouldBe([AuthConstants.Roles.Translator]);
    }

    public AccountAdministrationEndpointTests(AuthSystemApiFactory appFactory) : base(appFactory)
    {
    }

    private static string ReviewerPath(Guid accountId) => $"{AccountsPath}/{accountId}/reviewer";

    private static List<string> RelsOf(AccountListResponse response, IdentityId accountId) =>
        response.Items.Single(item => item.UserId == accountId.Value).Links.Select(link => link.Rel).ToList();

    private Task<(RegisterRequest Request, IdentityId Response)> RegisterAsync() =>
        UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, Password);

    private async Task<string> AdminAccessTokenAsync()
    {
        (RegisterRequest admin, _) = await RegisterAsync();
        await AccountStateFactory.AddToRoleAsync(Factory.Services, admin.Email, AuthConstants.Roles.Admin);
        return await GetAccessTokenAsync(admin.Email, Password);
    }

    private async Task<string> CallerAccessTokenAsync(CallerRole callerRole)
    {
        (RegisterRequest caller, _) = await RegisterAsync();
        if (callerRole is CallerRole.TranslatorAndReviewer)
        {
            await AccountStateFactory.AddToRoleAsync(Factory.Services, caller.Email, AuthConstants.Roles.Reviewer);
        }

        return await GetAccessTokenAsync(caller.Email, Password);
    }

    private async Task ApplyAsync(UngrantableAccount state, string email)
    {
        switch (state)
        {
            case UngrantableAccount.Admin:
                await AccountStateFactory.AddToRoleAsync(Factory.Services, email, AuthConstants.Roles.Admin);
                break;
            case UngrantableAccount.DeletionScheduled:
                await AccountStateFactory.ScheduleDeletionAsync(Factory.Services, email);
                break;
            case UngrantableAccount.WithoutTranslator:
                await AccountStateFactory.RemoveFromRoleAsync(Factory.Services, email, AuthConstants.Roles.Translator);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(state), state, null);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? accessToken)
    {
        using HttpRequestMessage request = new(method, new Uri(path, UriKind.RelativeOrAbsolute));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypes.HateoasJson));
        if (accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return await ApiClient.Http.SendAsync(request);
    }

    private Task<HttpStatusCode[]> SendOverlappingAsync(HttpMethod method, string path, string accessToken) =>
        Task.WhenAll(Enumerable.Range(0, OverlappingCalls).Select(async _ =>
        {
            using HttpResponseMessage response = await SendAsync(method, path, accessToken);
            return response.StatusCode;
        }));

    private async Task<AccountListResponse> GetAccountsAsync(string adminToken, string path = AccountsPath)
    {
        using HttpResponseMessage response = await SendAsync(HttpMethod.Get, path, adminToken);
        await response.EnsureSuccessWithDetailsAsync();
        return (await response.Content.ReadFromJsonAsync<AccountListResponse>(ApiClient.JsonOptions))!;
    }

    private async Task CreateAccountsAsync(int count)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        UserManager<ApplicationUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        for (int i = 0; i < count; i++)
        {
            ApplicationUser user = new()
            {
                UserName = string.Create(CultureInfo.InvariantCulture, $"pageduser{i:D2}"),
                Email = UserFactory.GenerateUniqueEmail(Faker)
            };
            IdentityResult result = await userManager.CreateAsync(user);
            if (!result.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Could not create paged test user {i}: {string.Join(", ", result.Errors.Select(error => error.Code))}.");
            }
        }
    }

    private async Task AnonymizeAsync(Guid accountId)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        string anonymizedEmail = $"{AnonymizationConstants.EmailPrefix}{Guid.NewGuid():N}{AnonymizationConstants.EmailDomain}";
        await db.Users
            .Where(user => user.Id == accountId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(user => user.Email, anonymizedEmail)
                .SetProperty(user => user.NormalizedEmail, anonymizedEmail.ToUpperInvariant())
                .SetProperty(user => user.PasswordHash, (string?)null));
    }

    private async Task MoveToTheMarkerDomainAsync(Guid accountId)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        string markerEmail = $"{AnonymizationConstants.EmailPrefix}{Guid.NewGuid():N}{AnonymizationConstants.EmailDomain}";
        await db.Users
            .Where(user => user.Id == accountId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(user => user.Email, markerEmail)
                .SetProperty(user => user.NormalizedEmail, markerEmail.ToUpperInvariant()));
    }

    private async Task<IList<string>> RolesOfAsync(Guid accountId)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        UserManager<ApplicationUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        ApplicationUser user = (await userManager.FindByIdAsync(accountId.ToString())).ShouldNotBeNull();
        return await userManager.GetRolesAsync(user);
    }

    private async Task<string> SecurityStampOfAsync(Guid accountId)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        UserManager<ApplicationUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        ApplicationUser user = (await userManager.FindByIdAsync(accountId.ToString())).ShouldNotBeNull();
        return (await userManager.GetSecurityStampAsync(user)).ShouldNotBeNull();
    }
}
