using System.Data.Common;
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
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Password;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.SharedKernel.Authorization;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

public sealed class AdminSeedingTests : EndpointsTestBase
{
    private const string AdminEmail = "admin@lotro-translator.pl";
    private const string AdminUsername = "seededadmin";
    private const string AdminPassword = "AdminTest123!";

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
            () => new NpgsqlException("The operation has timed out", new TimeoutException()));

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
            () => new NpgsqlException("The operation has timed out", new TimeoutException()));

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
    /// The seeder cannot tell the operator's own account from a stranger's registration at a mistyped
    /// address, so it never promotes an account it did not create (ADR-0056 amendment, #839).
    /// </summary>
    [Theory]
    [InlineData(AdminEmail, true)]
    [InlineData(AdminEmail, false)]
    [InlineData("Admin@Lotro-Translator.pl", true)]
    public async Task SeedAuthDatabase_ConfiguredEmailBelongsToAccountWithoutAdminRole_DoesNotPromoteIt(
        string existingEmail,
        bool emailConfirmed)
    {
        // Arrange
        await CreateTranslatorAsync(existingEmail, emailConfirmed);

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

    private async Task<Guid> CreateTranslatorAsync(string email, bool emailConfirmed)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        UserManager<ApplicationUser> userManager =
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        ApplicationUser translator = new()
        {
            UserName = "translatorone",
            Email = email,
            EmailConfirmed = emailConfirmed
        };

        (await userManager.CreateAsync(translator, "Translator123!")).Succeeded.ShouldBeTrue();
        (await userManager.AddToRoleAsync(translator, AuthConstants.Roles.Translator)).Succeeded.ShouldBeTrue();

        return translator.Id;
    }

    private static bool IsUserRoleInsert(DbCommand command) =>
        command.CommandText.Contains("INSERT INTO", StringComparison.Ordinal)
        && command.CommandText.Contains("\"UserRoles\"", StringComparison.Ordinal);

    private static bool WroteAUserRole(TransactionEndEventData eventData) =>
        eventData.Context is not null
        && eventData.Context.ChangeTracker.Entries<IdentityUserRole<Guid>>().Any();

    private static InvalidOperationException CreateSimulatedCrash() =>
        new("Simulated crash between the account and its role");

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
