using System.Data.Common;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using LotroKoniecDev.AuthSystem.API.Extensions;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Password;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.SharedKernel.Authorization;
using LotroKoniecDev.Tests.Shared;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

public sealed partial class AdminSeedingTests : EndpointsTestBase
{
    private const string AdminEmail = "admin@lotro-translator.pl";
    private const string AdminUsername = "seededadmin";
    private const string AdminPassword = "AdminTest123!";
    private const string TranslatorUsername = "translatorone";

    public AdminSeedingTests(AuthSystemApiFactory appFactory) : base(appFactory) { }

    [Fact]
    public async Task SeedAuthDatabase_WithAdminConfiguration_AdminAuthenticatesWithAdminRole()
    {
        // Arrange: the cleaner wipes users before every test, so re-run the (idempotent) seed
        await ReseedAsync();

        // Act: the seeded admin logs in by e-mail (ADR-0022)
        string accessToken = await GetAccessTokenAsync(AdminEmail, AdminPassword);

        // Assert
        accessToken.ShouldNotBeNullOrWhiteSpace();

        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        UserManager<ApplicationUser> userManager =
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        ApplicationUser? admin = await userManager.FindByEmailAsync(AdminEmail);
        admin.ShouldNotBeNull();
        admin.EmailConfirmed.ShouldBeTrue();
        (await userManager.IsInRoleAsync(admin, AuthConstants.Roles.Admin)).ShouldBeTrue();
    }

    [Fact]
    public async Task SeedAuthDatabase_RunTwice_SeedsExactlyOneAdmin()
    {
        // Arrange
        await ReseedAsync();

        // Act
        await ReseedAsync();

        // Assert
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        UserManager<ApplicationUser> userManager =
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        int adminCount = await userManager.Users.CountAsync(u => u.Email == AdminEmail);
        adminCount.ShouldBe(1);
    }

    /// <summary>
    /// The failure is not transient, so nothing retries it: it stands in for a process that dies between
    /// the account and its role (#839).
    /// </summary>
    [Fact]
    public async Task SeedAuthDatabase_AttemptFailsBetweenAccountAndRole_LeavesNoAccount()
    {
        // Arrange
        Factory.DbCommandFailures.FailNext(IsUserRoleInsert, CreateSimulatedCrash);

        // Act
        await Should.ThrowAsync<DbUpdateException>(() => ReseedAsync());

        // Assert
        Factory.DbCommandFailures.FailuresInjected.ShouldBe(1);

        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        UserManager<ApplicationUser> userManager =
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        (await userManager.FindByEmailAsync(AdminEmail)).ShouldBeNull();
    }

    [Fact]
    public async Task SeedAuthDatabase_AttemptFailsBetweenAccountAndRole_NextAttemptSeedsAdminWithAdminRole()
    {
        // Arrange
        Factory.DbCommandFailures.FailNext(IsUserRoleInsert, CreateSimulatedCrash);
        await Should.ThrowAsync<DbUpdateException>(() => ReseedAsync());
        Factory.DbCommandFailures.FailuresInjected.ShouldBe(1);

        // Act
        await ReseedAsync();

        // Assert
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        UserManager<ApplicationUser> userManager =
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        ApplicationUser? admin = await userManager.FindByEmailAsync(AdminEmail);
        admin.ShouldNotBeNull();
        (await userManager.IsInRoleAsync(admin, AuthConstants.Roles.Admin)).ShouldBeTrue();
    }

    /// <summary>
    /// EF replays the whole transaction after a transient failure. The replay must not write the first
    /// attempt's rows a second time.
    /// </summary>
    [Fact]
    public async Task SeedAuthDatabase_TransientFailureOnRoleWrite_SeedsExactlyOneAdminWithAdminRole()
    {
        // Arrange
        Factory.DbCommandFailures.FailNext(
            IsUserRoleInsert,
            CreateTransientFailure);

        // Act
        await ReseedAsync();

        // Assert
        Factory.DbCommandFailures.FailuresInjected.ShouldBe(1);

        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        UserManager<ApplicationUser> userManager =
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        IList<ApplicationUser> admins = await userManager.GetUsersInRoleAsync(AuthConstants.Roles.Admin);
        admins.ShouldHaveSingleItem().Email.ShouldBe(AdminEmail);
    }

    /// <summary>
    /// The commit lands, but its answer is lost on the way back, so EF replays the transaction. The replay
    /// must find the admin it just saved and leave it alone.
    /// </summary>
    [Fact]
    public async Task SeedAuthDatabase_CommitLandsButItsAnswerIsLost_SeedsExactlyOneAdminWithAdminRole()
    {
        // Arrange
        Factory.DbCommitFailures.FailNextCommitAfterItLands(
            WroteAUserRole,
            CreateTransientFailure);

        // Act
        await ReseedAsync();

        // Assert
        Factory.DbCommitFailures.FailuresInjected.ShouldBe(1);

        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        UserManager<ApplicationUser> userManager =
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        IList<ApplicationUser> admins = await userManager.GetUsersInRoleAsync(AuthConstants.Roles.Admin);
        admins.ShouldHaveSingleItem().Email.ShouldBe(AdminEmail);
    }

    /// <summary>
    /// Both recoveries above would end the same way if <c>ColdStartRetry</c> re-ran the whole seed
    /// instead. This pins that EF's execution strategy replays the transaction inside the attempt.
    /// </summary>
    [Theory]
    [InlineData("role write")]
    [InlineData("commit")]
    public async Task SeedAuthDatabase_TransientFailure_RecoversWithoutAColdStartRetry(string failurePoint)
    {
        // Arrange
        ArmTransientFailure(failurePoint);
        using CapturingLoggerFactory loggerFactory = new();

        // Act
        await ReseedAsync(loggerFactory);

        // Assert
        (Factory.DbCommandFailures.FailuresInjected + Factory.DbCommitFailures.FailuresInjected).ShouldBe(1);
        loggerFactory.Entries.ShouldNotContain(e => e.EventId.Id == EventIds.StartupTransientDatabaseFailure);
    }

    /// <summary>
    /// The last row has the shape of an admin half-made before #839: the configured username, confirmed,
    /// no password. A "repair a half-made admin" shortcut would promote exactly that row, so it is pinned
    /// too (ADR-0056 amendment).
    /// </summary>
    [Theory]
    [InlineData(AdminEmail, TranslatorUsername, true, true)]
    [InlineData(AdminEmail, TranslatorUsername, false, true)]
    [InlineData("Admin@Lotro-Translator.pl", TranslatorUsername, true, true)]
    [InlineData(AdminEmail, AdminUsername, true, false)]
    public async Task SeedAuthDatabase_ConfiguredEmailBelongsToAccountWithoutAdminRole_DoesNotPromoteIt(
        string existingEmail,
        string existingUsername,
        bool emailConfirmed,
        bool hasPassword)
    {
        // Arrange
        await CreateTranslatorAsync(existingEmail, emailConfirmed, hasPassword, existingUsername);

        // Act
        await ReseedAsync();

        // Assert
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        UserManager<ApplicationUser> userManager =
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        IList<ApplicationUser> admins = await userManager.GetUsersInRoleAsync(AuthConstants.Roles.Admin);
        admins.ShouldBeEmpty();
    }

    [Fact]
    public async Task SeedAuthDatabase_ConfiguredEmailBelongsToAccountWithoutAdminRole_LogsWarning2354()
    {
        // Arrange
        Guid translatorId = await CreateTranslatorAsync(AdminEmail, emailConfirmed: true);
        using CapturingLoggerFactory loggerFactory = new();

        // Act
        await ReseedAsync(loggerFactory);

        // Assert
        CapturingLoggerFactory.LogEntry warning = loggerFactory.Entries
            .Where(e => e.EventId.Id == EventIds.AdminSeedEmailTakenWithoutAdminRole)
            .ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldContain(translatorId.ToString());
    }

    /// <summary>
    /// The translator's username differs from the admin's, so no other check stops the seed. Only the undo
    /// reservation does (#849).
    /// </summary>
    [Fact]
    public async Task SeedAuthDatabase_ConfiguredEmailIsAnotherAccountsArmedUndoTarget_SeedsNoAdmin()
    {
        // Arrange
        await MoveTranslatorOffAdminEmailAsync();

        // Act
        await ReseedAsync();

        // Assert
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        UserManager<ApplicationUser> userManager =
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        (await userManager.FindByEmailAsync(AdminEmail)).ShouldBeNull();
        (await userManager.GetUsersInRoleAsync(AuthConstants.Roles.Admin)).ShouldBeEmpty();
    }

    [Fact]
    public async Task SeedAuthDatabase_ConfiguredEmailIsAnotherAccountsArmedUndoTarget_LogsWarning2355()
    {
        // Arrange
        await MoveTranslatorOffAdminEmailAsync();
        using CapturingLoggerFactory loggerFactory = new();

        // Act
        await ReseedAsync(loggerFactory);

        // Assert
        loggerFactory.Entries
            .Where(e => e.EventId.Id == EventIds.AdminSeedEmailReservedForUndo)
            .ShouldHaveSingleItem()
            .Level.ShouldBe(LogLevel.Warning);
    }

    /// <summary>
    /// What the reservation protects: after a restart, the owner can still take the address back.
    /// </summary>
    [Fact]
    public async Task SeedAuthDatabase_ConfiguredEmailIsAnotherAccountsArmedUndoTarget_UndoLinkStillRestoresTheAddress()
    {
        // Arrange
        (Guid userId, string newEmail, string revertToken) = await MoveTranslatorOffAdminEmailAsync();
        await ReseedAsync();

        // Act
        await PostToPageAsync(
            "/Account/RevertEmailChange",
            $"/Account/RevertEmailChange?userId={userId}&from={Uri.EscapeDataString(AdminEmail)}"
            + $"&to={Uri.EscapeDataString(newEmail)}&token={Uri.EscapeDataString(revertToken)}",
            new Dictionary<string, string>
            {
                ["UserId"] = userId.ToString(),
                ["From"] = AdminEmail,
                ["To"] = newEmail,
                ["Token"] = revertToken
            });

        // Assert
        (await LoadUserAsync(userId)).Email.ShouldBe(AdminEmail);
    }

    /// <summary>
    /// The admin changing its own address is the common case. The username check runs first, so the
    /// operator keeps getting 2353, which ADR-0056 decision 5 and the runbook explain, and not 2355.
    /// </summary>
    [Fact]
    public async Task SeedAuthDatabase_SeededAdminMovedOffConfiguredEmail_LogsOnlyWarning2353()
    {
        // Arrange
        await ReseedAsync();
        Guid adminId = await AdminIdAsync();
        await MoveOffAdminEmailAsync(adminId, AdminPassword);
        using CapturingLoggerFactory loggerFactory = new();

        // Act
        await ReseedAsync(loggerFactory);

        // Assert
        loggerFactory.Entries
            .Where(e => e.Level >= LogLevel.Warning)
            .ShouldHaveSingleItem()
            .EventId.Id.ShouldBe(EventIds.AdminSeedUsernameTaken);
    }

    /// <summary>
    /// The reservation ends with the undo link it protects, so the admin is seeded on the next start
    /// after that.
    /// </summary>
    [Fact]
    public async Task SeedAuthDatabase_UndoWindowOfTheConfiguredEmailHasClosed_SeedsAdminWithAdminRole()
    {
        // Arrange
        (Guid userId, _, _) = await MoveTranslatorOffAdminEmailAsync();
        await BackdateUndoArmingAsync(userId, TimeSpan.FromDays(15));

        // Act
        await ReseedAsync();

        // Assert
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        UserManager<ApplicationUser> userManager =
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        IList<ApplicationUser> admins = await userManager.GetUsersInRoleAsync(AuthConstants.Roles.Admin);
        admins.ShouldHaveSingleItem().Email.ShouldBe(AdminEmail);
    }

    /// <summary>
    /// Every restart of a box finds its admin in place. That normal case must not raise a warning.
    /// </summary>
    [Fact]
    public async Task SeedAuthDatabase_AdminAlreadySeeded_LogsNoWarning()
    {
        // Arrange
        await ReseedAsync();
        using CapturingLoggerFactory loggerFactory = new();

        // Act
        await ReseedAsync(loggerFactory);

        // Assert
        loggerFactory.Entries.ShouldNotContain(e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task SeedAuthDatabase_UsernameAlreadyTaken_SkipsAdminSeedingWithoutCrashing()
    {
        // Arrange: a regular user grabs the admin username before the seed runs
        await using (AsyncServiceScope scope = Factory.Services.CreateAsyncScope())
        {
            UserManager<ApplicationUser> userManager =
                scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

            ApplicationUser squatter = new()
            {
                UserName = AdminUsername,
                Email = "squatter@lotro-translator.pl",
                EmailConfirmed = true
            };

            IdentityResult createResult = await userManager.CreateAsync(squatter, "Squatter123!");
            createResult.Succeeded.ShouldBeTrue();
        }

        // Act
        await ReseedAsync();

        // Assert: seeding skipped instead of crashing the startup path
        await using AsyncServiceScope assertScope = Factory.Services.CreateAsyncScope();
        UserManager<ApplicationUser> assertUserManager =
            assertScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        ApplicationUser? admin = await assertUserManager.FindByEmailAsync(AdminEmail);
        admin.ShouldBeNull();
    }

    [Fact]
    public async Task CreateUser_ShouldFailWithInvalidUserName_WhenUsernameViolatesAllowedCharacters()
    {
        // Identity's AllowedUserNameCharacters (UsernameConstants) is the last-resort layer that
        // also guards the seeder: a mis-configured AdminUser:Username surfaces THIS error as the
        // loud startup failure documented in the runbook (ADR-0022).
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        UserManager<ApplicationUser> userManager =
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        ApplicationUser user = new()
        {
            UserName = "bad-admin",
            Email = "badadmin@lotro-translator.pl",
            EmailConfirmed = true
        };

        IdentityResult result = await userManager.CreateAsync(user, AdminPassword);

        result.Succeeded.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Code == "InvalidUserName");
    }

    /// <summary>
    /// Every box runs outside Development and Testing, so this is the seed a fresh deploy gets. The factory
    /// still configures AdminUser:Password, which is the case the seeder has to ignore (ADR-0056).
    /// </summary>
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task SeedAuthDatabase_OutsideDevelopmentAndTesting_SeedsConfirmedAdminWithoutPassword(
        string environmentName)
    {
        // Act
        await ReseedAsync(new FakeWebHostEnvironment(environmentName));

        // Assert
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        UserManager<ApplicationUser> userManager =
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        ApplicationUser? admin = await userManager.FindByEmailAsync(AdminEmail);
        admin.ShouldNotBeNull();
        admin.EmailConfirmed.ShouldBeTrue();
        (await userManager.IsInRoleAsync(admin, AuthConstants.Roles.Admin)).ShouldBeTrue();
        (await userManager.HasPasswordAsync(admin)).ShouldBeFalse();
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task SeedAuthDatabase_OutsideDevelopmentAndTesting_ConfiguredPasswordDoesNotSignIn(
        string environmentName)
    {
        // Arrange
        await ReseedAsync(new FakeWebHostEnvironment(environmentName));

        // Act
        HttpResponseMessage tokenResponse = await RequestPasswordGrantAsync(AdminEmail, AdminPassword);

        // Assert: the admin exists, so the refusal is about the password and not about a missing account
        tokenResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        UserManager<ApplicationUser> userManager =
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        (await userManager.FindByEmailAsync(AdminEmail)).ShouldNotBeNull();
    }

    /// <summary>
    /// The runbook's first sign-in and rotation procedure: a mail to the admin address is the only way to
    /// give the seeded admin a password.
    /// </summary>
    [Fact]
    public async Task SeedAuthDatabase_OutsideDevelopmentAndTesting_AdminSignsInAfterSettingPasswordThroughReset()
    {
        // Arrange
        const string chosenPassword = "OperatorChosen1!";
        await ReseedAsync(new FakeWebHostEnvironment("Production"));
        PasswordResetEmailSpy.Reset();

        await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/forgot-password", UriKind.Relative),
            new ForgotPasswordRequest(AdminEmail));
        await PasswordResetEmailSpy.WaitForCaptureAsync();

        await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/reset-password", UriKind.Relative),
            new ResetPasswordRequest(AdminEmail, PasswordResetEmailSpy.LastResetToken!, chosenPassword));

        // Act
        HttpResponseMessage tokenResponse = await RequestPasswordGrantAsync(AdminEmail, chosenPassword);

        // Assert
        tokenResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private async Task ReseedAsync()
    {
        IWebHostEnvironment environment = Factory.Services.GetRequiredService<IWebHostEnvironment>();
        await ReseedAsync(environment);
    }

    private async Task ReseedAsync(IWebHostEnvironment environment)
    {
        await DatabaseSeederExtensions.SeedAuthDatabaseAsync(Factory.Services, environment);
    }

    private async Task ReseedAsync(ILoggerFactory loggerFactory)
    {
        IWebHostEnvironment environment = Factory.Services.GetRequiredService<IWebHostEnvironment>();
        await DatabaseSeederExtensions.SeedAuthDatabaseAsync(
            new ServicesWithLoggerFactory(Factory.Services, loggerFactory),
            environment);
    }

    private async Task<Guid> CreateTranslatorAsync(
        string email,
        bool emailConfirmed,
        bool hasPassword = true,
        string username = TranslatorUsername)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        UserManager<ApplicationUser> userManager =
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        ApplicationUser translator = new()
        {
            UserName = username,
            Email = email,
            EmailConfirmed = emailConfirmed
        };

        IdentityResult createResult = hasPassword
            ? await userManager.CreateAsync(translator, "Translator123!")
            : await userManager.CreateAsync(translator);
        createResult.Succeeded.ShouldBeTrue();
        (await userManager.AddToRoleAsync(translator, AuthConstants.Roles.Translator)).Succeeded.ShouldBeTrue();

        return translator.Id;
    }

    private async Task<(Guid UserId, string NewEmail, string RevertToken)> MoveTranslatorOffAdminEmailAsync()
    {
        Guid userId = await CreateTranslatorAsync(AdminEmail, emailConfirmed: true);
        (string newEmail, string revertToken) = await MoveOffAdminEmailAsync(userId, "Translator123!");

        return (userId, newEmail, revertToken);
    }

    /// <summary>
    /// Moves an account off the admin address the ordinary way: request, then confirm. The confirm arms
    /// an undo link back to the admin address, so that address stays reserved for the account.
    /// </summary>
    private async Task<(string NewEmail, string RevertToken)> MoveOffAdminEmailAsync(Guid userId, string password)
    {
        string accessToken = await GetAccessTokenAsync(AdminEmail, password);
        string newEmail = Faker.Internet.Email();

        using HttpRequestMessage changeRequest = new(HttpMethod.Post, "auth/account/change-email");
        changeRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        changeRequest.Content = JsonContent.Create(new ChangeEmailRequest(newEmail, password));
        (await ApiClient.Http.SendAsync(changeRequest)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await EmailChangeEmailSpy.WaitForVerificationCaptureAsync();

        string confirmToken = EmailChangeEmailSpy.LastVerificationToken!;
        await PostToPageAsync(
            "/Account/ConfirmEmailChange",
            $"/Account/ConfirmEmailChange?userId={userId}&email={Uri.EscapeDataString(newEmail)}"
            + $"&token={Uri.EscapeDataString(confirmToken)}",
            new Dictionary<string, string>
            {
                ["UserId"] = userId.ToString(),
                ["Email"] = newEmail,
                ["Token"] = confirmToken
            });
        await EmailChangeEmailSpy.WaitForRevertOfferCaptureAsync();
        await EmailChangeEmailSpy.WaitForChangedNoticeCaptureAsync();

        // The confirm page answers 200 when it refuses too. Without this check a failed move would leave
        // the account on the admin address, and the seed would stop at 2354 without testing the
        // reservation at all.
        (await LoadUserAsync(userId)).Email.ShouldBe(newEmail);

        return (newEmail, EmailChangeEmailSpy.LastRevertToken!);
    }

    private async Task<Guid> AdminIdAsync()
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        UserManager<ApplicationUser> userManager =
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        return (await userManager.FindByEmailAsync(AdminEmail))!.Id;
    }

    private async Task<ApplicationUser> LoadUserAsync(Guid userId)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        return await db.Users.AsNoTracking().SingleAsync(user => user.Id == userId);
    }

    private async Task BackdateUndoArmingAsync(Guid userId, TimeSpan age)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        ApplicationUser user = await db.Users.SingleAsync(row => row.Id == userId);
        user.EmailChangeRevertArmedAt = DateTimeOffset.UtcNow - age;
        await db.SaveChangesAsync();
    }

    private async Task PostToPageAsync(string pagePath, string getUrl, Dictionary<string, string> formFields)
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

        HttpResponseMessage response = await ApiClient.Http.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private void ArmTransientFailure(string failurePoint)
    {
        switch (failurePoint)
        {
            case "role write":
                Factory.DbCommandFailures.FailNext(IsUserRoleInsert, CreateTransientFailure);
                break;
            case "commit":
                Factory.DbCommitFailures.FailNextCommitAfterItLands(WroteAUserRole, CreateTransientFailure);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(failurePoint), failurePoint, null);
        }
    }

    private static NpgsqlException CreateTransientFailure() =>
        new("The operation has timed out", new TimeoutException());

    private static bool IsUserRoleInsert(DbCommand command) =>
        command.CommandText.Contains("INSERT INTO", StringComparison.Ordinal)
        && command.CommandText.Contains("\"UserRoles\"", StringComparison.Ordinal);

    private static bool WroteAUserRole(TransactionEndEventData eventData) =>
        eventData.Context is not null
        && eventData.Context.ChangeTracker.Entries<IdentityUserRole<Guid>>().Any();

    private static InvalidOperationException CreateSimulatedCrash() =>
        new("Simulated crash between the account and its role");

    [GeneratedRegex("""name="__RequestVerificationToken".*?value="([^"]+)""")]
    private static partial Regex AntiForgeryTokenRegex();

    /// <summary>
    /// The test host's services with one change: the seeder's logger writes to the given factory. The
    /// seeder takes its logger from the services it is handed, and the host's own log cannot be read
    /// back.
    /// </summary>
    private sealed class ServicesWithLoggerFactory : IServiceProvider
    {
        private readonly IServiceProvider _services;
        private readonly ILoggerFactory _loggerFactory;

        public ServicesWithLoggerFactory(IServiceProvider services, ILoggerFactory loggerFactory)
        {
            _services = services;
            _loggerFactory = loggerFactory;
        }

        public object? GetService(Type serviceType) =>
            serviceType == typeof(ILoggerFactory) ? _loggerFactory : _services.GetService(serviceType);
    }
}
