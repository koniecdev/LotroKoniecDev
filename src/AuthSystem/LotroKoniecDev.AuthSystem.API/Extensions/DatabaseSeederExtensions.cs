using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using LotroKoniecDev.AuthSystem.API.Services.Accounts;
using LotroKoniecDev.AuthSystem.API.Settings;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationRoles.Entities;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.SharedKernel.Authorization;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace LotroKoniecDev.AuthSystem.API.Extensions;

internal static partial class DatabaseSeederExtensions
{
    public const string DefaultAdminUsername = "admin";

    public static async Task SeedAuthDatabaseAsync(this WebApplication app)
    {
        await SeedAuthDatabaseAsync(app.Services, app.Environment);
    }

    internal static async Task SeedAuthDatabaseAsync(IServiceProvider services, IWebHostEnvironment environment)
    {
        ILogger logger = services.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(DatabaseSeederExtensions));

        // Read once, before the retry, so a leftover password logs its warning once per start.
        IConfiguration configuration = services.GetRequiredService<IConfiguration>();
        string? adminEmail = configuration["AdminUser:Email"];
        string adminUsername = ReadAdminUsername(configuration);
        string? adminPassword = ReadBootstrapPassword(configuration, environment, logger);

        OpenIddictSettings openIddictSettings = services.GetRequiredService<IOptions<OpenIddictSettings>>().Value;

        // The seed is safe to repeat. Each attempt gets a new scope, because a context that failed
        // during a migration must not be reused.
        await ColdStartRetry.ExecuteAsync(
            async () =>
            {
                using IServiceScope scope = services.CreateScope();

                AuthDbContext dbContext = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
                await dbContext.Database.MigrateAsync();

                await SeedRolesAsync(scope.ServiceProvider);
                await SeedAdminUserAsync(scope.ServiceProvider, adminEmail, adminUsername, adminPassword, logger);
                await SeedOAuthApplicationsAsync(scope.ServiceProvider, openIddictSettings, environment);
            },
            logger);
    }

    private static async Task SeedRolesAsync(IServiceProvider serviceProvider)
    {
        RoleManager<ApplicationRole> roleManager = serviceProvider.GetRequiredService<RoleManager<ApplicationRole>>();

        string[] roles = [AuthConstants.Roles.Admin, AuthConstants.Roles.Translator];

        foreach (string roleName in roles)
        {
            if (await roleManager.RoleExistsAsync(roleName))
            {
                continue;
            }

            ApplicationRole role = new() { Name = roleName };

            await roleManager.CreateAsync(role);
        }
    }

    private static async Task SeedAdminUserAsync(
        IServiceProvider serviceProvider,
        string? email,
        string username,
        string? password,
        ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return;
        }

        // One transaction, so a failure cannot leave an admin without its role (ADR-0056). With
        // EnableRetryOnFailure on, EF refuses our own transaction outside the execution strategy.
        AuthDbContext dbContext = serviceProvider.GetRequiredService<AuthDbContext>();
        IExecutionStrategy executionStrategy = dbContext.Database.CreateExecutionStrategy();

        await executionStrategy.ExecuteAsync(async () =>
            await SeedAdminUserInTransactionAsync(serviceProvider, dbContext, email, username, password, logger));
    }

    private static async Task SeedAdminUserInTransactionAsync(
        IServiceProvider serviceProvider,
        AuthDbContext dbContext,
        string email,
        string username,
        string? password,
        ILogger logger)
    {
        // After a rollback the change tracker still holds the previous attempt's rows.
        dbContext.ChangeTracker.Clear();

        await using IDbContextTransaction transaction = await dbContext.Database.BeginTransactionAsync();

        UserManager<ApplicationUser> userManager = serviceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        // Never promotes an account it did not create: it cannot tell the operator's account from a
        // stranger's (ADR-0056 amendment).
        ApplicationUser? existingUser = await userManager.FindByEmailAsync(email);
        if (existingUser is not null)
        {
            if (!await userManager.IsInRoleAsync(existingUser, AuthConstants.Roles.Admin))
            {
                LogAdminSeedEmailTakenWithoutAdminRole(logger, existingUser.Id);
            }

            return;
        }

        // A taken username must not break every later startup; the operator frees it or changes AdminUser.
        if (await userManager.FindByNameAsync(username) is not null)
        {
            LogAdminSeedUsernameTaken(logger, username);
            return;
        }

        // An admin seeded on an address held for an undo would make that undo link fail (#849). After
        // the username check, so an admin that moved off the address keeps getting 2353 (ADR-0056
        // decision 5).
        IEmailChangeRevertReservation revertReservation =
            serviceProvider.GetRequiredService<IEmailChangeRevertReservation>();
        if (await revertReservation.IsReservedAsync(email, exceptUserId: null, CancellationToken.None))
        {
            LogAdminSeedEmailReservedForUndo(logger);
            return;
        }

        TimeProvider timeProvider = serviceProvider.GetRequiredService<TimeProvider>();

        ApplicationUser admin = new()
        {
            UserName = username,
            Email = email,
            EmailConfirmed = true,
            PrivacyPolicyAccepted = true,
            PrivacyPolicyAcceptedDate = timeProvider.GetUtcNow(),
            TermsOfServiceAccepted = true,
            TermsOfServiceAcceptedDate = timeProvider.GetUtcNow()
        };

        IdentityResult createResult = password is null
            ? await userManager.CreateAsync(admin)
            : await userManager.CreateAsync(admin, password);
        if (!createResult.Succeeded)
        {
            string errors = string.Join(", ", createResult.Errors.Select(e => e.Description));
            throw new InvalidOperationException($"Admin user seeding failed: {errors}");
        }

        IdentityResult roleResult = await userManager.AddToRoleAsync(admin, AuthConstants.Roles.Admin);
        if (!roleResult.Succeeded)
        {
            string errors = string.Join(", ", roleResult.Errors.Select(e => e.Description));
            throw new InvalidOperationException($"Admin role assignment failed: {errors}");
        }

        await transaction.CommitAsync();

        if (password is null)
        {
            LogAdminSeededWithoutPassword(logger, admin.Id);
        }
    }

    /// <summary>
    /// Compose passes an unset AUTH_ADMIN_USERNAME as an empty string, and Identity refuses an empty
    /// username, which would crash every startup. So a blank value falls back to the default.
    /// </summary>
    internal static string ReadAdminUsername(IConfiguration configuration)
    {
        string? username = configuration["AdminUser:Username"];

        return string.IsNullOrWhiteSpace(username) ? DefaultAdminUsername : username;
    }

    /// <summary>
    /// Only Development and Testing take the admin password from configuration. Elsewhere the operator
    /// sets it through the password reset mail, so no file on a box holds it (ADR-0056).
    /// </summary>
    internal static string? ReadBootstrapPassword(
        IConfiguration configuration,
        IWebHostEnvironment environment,
        ILogger logger)
    {
        string? password = configuration["AdminUser:Password"];

        if (string.IsNullOrWhiteSpace(password))
        {
            return null;
        }

        if (environment.IsDevelopment() || environment.IsTesting())
        {
            return password;
        }

        LogAdminSeedPasswordIgnored(logger, environment.EnvironmentName);
        return null;
    }

    private static async Task SeedOAuthApplicationsAsync(
        IServiceProvider serviceProvider,
        OpenIddictSettings settings,
        IWebHostEnvironment environment)
    {
        IOpenIddictApplicationManager applicationManager =
            serviceProvider.GetRequiredService<IOpenIddictApplicationManager>();

        if (await applicationManager.FindByClientIdAsync(AuthConstants.ClientIds.Web) is null)
        {
            OpenIddictApplicationDescriptor webClient = new()
            {
                ClientId = AuthConstants.ClientIds.Web,
                ClientSecret = null,
                DisplayName = "LotroKoniecDev Web Application",
                ConsentType = ConsentTypes.Implicit,
                ClientType = ClientTypes.Public,
                Permissions =
                {
                    Permissions.Endpoints.Authorization,
                    Permissions.Endpoints.Token,
                    Permissions.Endpoints.EndSession,
                    Permissions.Endpoints.Revocation,
                    Permissions.GrantTypes.AuthorizationCode,
                    Permissions.GrantTypes.RefreshToken,
                    Permissions.ResponseTypes.Code,
                    Permissions.Scopes.Email,
                    Permissions.Scopes.Profile,
                    Permissions.Scopes.Roles,
                    Permissions.Prefixes.Scope + AuthConstants.Scopes.Api,
                    Permissions.Prefixes.Scope + Scopes.OfflineAccess
                }
            };

            // Written only when the client is created, so a later change needs a reseed (runbook,
            // "Reseed traps").
            foreach (string redirectUri in settings.WebClient.RedirectUris)
            {
                webClient.RedirectUris.Add(new Uri(redirectUri));
            }

            foreach (string postLogoutRedirectUri in settings.WebClient.PostLogoutRedirectUris)
            {
                webClient.PostLogoutRedirectUris.Add(new Uri(postLogoutRedirectUri));
            }

            await applicationManager.CreateAsync(webClient);
        }

        if (await applicationManager.FindByClientIdAsync(AuthConstants.ClientIds.Api) is null)
        {
            await applicationManager.CreateAsync(new OpenIddictApplicationDescriptor
            {
                ClientId = AuthConstants.ClientIds.Api,
                ClientSecret = settings.ApiClientSecret,
                DisplayName = "LotroKoniecDev API Service",
                ClientType = ClientTypes.Confidential,
                Permissions =
                {
                    Permissions.Endpoints.Token,
                    Permissions.Endpoints.Introspection,
                    Permissions.GrantTypes.ClientCredentials,
                    Permissions.Prefixes.Scope + AuthConstants.Scopes.Api,
                    Permissions.Prefixes.Scope + AuthConstants.Scopes.Service
                }
            });
        }

        // The password-flow client for the integration and E2E tests.
        if (environment.IsTesting())
        {
            const string testClientId = "lotrokoniecdev-test";
            if (await applicationManager.FindByClientIdAsync(testClientId) is null)
            {
                await applicationManager.CreateAsync(new OpenIddictApplicationDescriptor
                {
                    ClientId = testClientId,
                    ClientSecret = null,
                    DisplayName = "LotroKoniecDev Test Client",
                    ClientType = ClientTypes.Public,
                    Permissions =
                    {
                        Permissions.Endpoints.Token,
                        Permissions.Endpoints.Revocation,
                        Permissions.GrantTypes.Password,
                        Permissions.GrantTypes.RefreshToken,
                        Permissions.Scopes.Email,
                        Permissions.Scopes.Profile,
                        Permissions.Scopes.Roles,
                        Permissions.Prefixes.Scope + AuthConstants.Scopes.Api,
                        Permissions.Prefixes.Scope + Scopes.OfflineAccess
                    }
                });
            }
        }
    }

    [LoggerMessage(EventId = EventIds.AdminSeededWithoutPassword, Level = LogLevel.Information, Message = "Seeded the admin account {UserId} without a password. Set the first password through the password reset page")]
    private static partial void LogAdminSeededWithoutPassword(ILogger logger, Guid userId);

    [LoggerMessage(EventId = EventIds.AdminSeedPasswordIgnored, Level = LogLevel.Warning, Message = "AdminUser:Password is ignored in the {EnvironmentName} environment. Remove it from the configuration and set the admin password through the password reset page")]
    private static partial void LogAdminSeedPasswordIgnored(ILogger logger, string environmentName);

    [LoggerMessage(EventId = EventIds.AdminSeedUsernameTaken, Level = LogLevel.Warning, Message = "Admin seeding skipped: the username {Username} already belongs to an account with a different e-mail address")]
    private static partial void LogAdminSeedUsernameTaken(ILogger logger, string username);

    [LoggerMessage(EventId = EventIds.AdminSeedEmailTakenWithoutAdminRole, Level = LogLevel.Warning, Message = "Admin seeding skipped: the configured e-mail address belongs to account {UserId}, which does not have the Admin role. The seeder never promotes an existing account")]
    private static partial void LogAdminSeedEmailTakenWithoutAdminRole(ILogger logger, Guid userId);

    [LoggerMessage(EventId = EventIds.AdminSeedEmailReservedForUndo, Level = LogLevel.Warning, Message = "Admin seeding skipped: an existing account can still undo an e-mail change back to the configured e-mail address. The address stays reserved until that undo link expires")]
    private static partial void LogAdminSeedEmailReservedForUndo(ILogger logger);
}
