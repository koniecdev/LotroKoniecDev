using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using LotroKoniecDev.AuthSystem.API.Extensions;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
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
        // Arrange — the cleaner wipes users before every test, so re-run the (idempotent) seed
        await ReseedAsync();

        // Act — the seeded admin logs in by e-mail (ADR-0022)
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

    [Fact]
    public async Task SeedAuthDatabase_UsernameAlreadyTaken_SkipsAdminSeedingWithoutCrashing()
    {
        // Arrange — a regular user grabs the admin username before the seed runs
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

        // Assert — seeding skipped instead of crashing the startup path
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

    private async Task ReseedAsync()
    {
        IWebHostEnvironment environment = Factory.Services.GetRequiredService<IWebHostEnvironment>();
        await DatabaseSeederExtensions.SeedAuthDatabaseAsync(Factory.Services, environment);
    }
}
