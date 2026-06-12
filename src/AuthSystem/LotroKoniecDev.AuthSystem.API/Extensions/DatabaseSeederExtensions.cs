using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using LotroKoniecDev.AuthSystem.API.Settings;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationRoles.Entities;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.SharedKernel.Authorization;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace LotroKoniecDev.AuthSystem.API.Extensions;

internal static class DatabaseSeederExtensions
{
    public static async Task SeedAuthDatabaseAsync(this WebApplication app)
    {
        await SeedAuthDatabaseAsync(app.Services, app.Environment);
    }

    internal static async Task SeedAuthDatabaseAsync(IServiceProvider services, IWebHostEnvironment environment)
    {
        using IServiceScope scope = services.CreateScope();

        AuthDbContext dbContext = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        await dbContext.Database.MigrateAsync();

        await SeedRolesAsync(scope.ServiceProvider);
        await SeedAdminUserAsync(scope.ServiceProvider);
        await SeedOAuthApplicationsAsync(scope.ServiceProvider, environment);
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

    private static async Task SeedAdminUserAsync(IServiceProvider serviceProvider)
    {
        IConfiguration configuration = serviceProvider.GetRequiredService<IConfiguration>();

        // Credentials come from configuration ("AdminUser" section: env vars in production,
        // appsettings.Development.json locally). No credentials configured = no admin seeded.
        string? email = configuration["AdminUser:Email"];
        string? password = configuration["AdminUser:Password"];
        string username = configuration["AdminUser:Username"] ?? "admin";

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        UserManager<ApplicationUser> userManager = serviceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        if (await userManager.FindByEmailAsync(email) is not null)
        {
            return;
        }

        // A taken username must not crash every subsequent startup — skip and leave
        // resolution (freeing the username or reconfiguring AdminUser) to the operator.
        if (await userManager.FindByNameAsync(username) is not null)
        {
            return;
        }

        TimeProvider timeProvider = serviceProvider.GetRequiredService<TimeProvider>();

        ApplicationUser admin = new()
        {
            UserName = username,
            Email = email,
            EmailConfirmed = true,
            DataProcessingConsentGiven = true,
            DataProcessingConsentDate = timeProvider.GetUtcNow(),
            PrivacyPolicyAccepted = true,
            PrivacyPolicyAcceptedDate = timeProvider.GetUtcNow()
        };

        IdentityResult createResult = await userManager.CreateAsync(admin, password);
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
    }

    private static async Task SeedOAuthApplicationsAsync(IServiceProvider serviceProvider, IWebHostEnvironment environment)
    {
        IOpenIddictApplicationManager applicationManager =
            serviceProvider.GetRequiredService<IOpenIddictApplicationManager>();

        OpenIddictSettings settings = serviceProvider
            .GetRequiredService<IOptions<OpenIddictSettings>>().Value;

        bool isProduction = !environment.IsDevelopment() && !environment.IsEnvironment("Testing");

        // Web Application Client
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

            // Only add localhost URIs in development/testing
            if (!isProduction)
            {
                webClient.RedirectUris.Add(new Uri(settings.WebClient.RedirectUris[0]));
                webClient.PostLogoutRedirectUris.Add(new Uri(settings.WebClient.PostLogoutRedirectUris[0]));
            }

            // Production URIs from configuration
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

        // API Client (for service-to-service communication)
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

        // Test Client (for integration/E2E tests only) - uses password flow
        // This client is only seeded in Testing environment.
        if (environment.IsEnvironment("Testing"))
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
}
