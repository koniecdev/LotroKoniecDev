using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationRoles.Entities;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.AuthSystem.Persistence.Identity;
using LotroKoniecDev.AuthSystem.Persistence.Settings;
using LotroKoniecDev.Options;
using LotroKoniecDev.SharedKernel.Constants;
namespace LotroKoniecDev.AuthSystem.Persistence;

public static class PersistenceDependencyInjection
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddAuthPersistence()
        {
            services.AddSingleton<IValidator<ConnectionStringSettings>, ConnectionStringSettingsValidator>();
            services.AddOptionsWithFluentValidation<ConnectionStringSettings>(ConnectionStringSettings.ConfigurationSection);

            services.AddDbContext<AuthDbContext>((sp, options) =>
            {
                options.UseNpgsql(
                    sp.GetRequiredService<IOptions<ConnectionStringSettings>>().Value.AuthDatabase,
                    npgsqlOptions =>
                    {
                        npgsqlOptions.EnableRetryOnFailure(
                            maxRetryCount: 3,
                            maxRetryDelay: TimeSpan.FromSeconds(10),
                            errorCodesToAdd: null);
                        npgsqlOptions.CommandTimeout(30);
                        npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", DatabaseSchemas.Auth);
                    });

                options.UseOpenIddict();
            });

            services.AddIdentityCore<ApplicationUser>(options =>
                {
                    options.Password.RequireDigit = true;
                    options.Password.RequireLowercase = true;
                    options.Password.RequireUppercase = true;
                    options.Password.RequireNonAlphanumeric = true;
                    options.Password.RequiredLength = 8;
                    options.User.RequireUniqueEmail = true;
                    options.User.AllowedUserNameCharacters = UsernameConstants.AllowedCharacters;
                    options.SignIn.RequireConfirmedEmail = true;
                    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
                    options.Lockout.MaxFailedAccessAttempts = 5;
                })
                .AddRoles<ApplicationRole>()
                .AddSignInManager()
                .AddDefaultTokenProviders()
                .AddTokenProvider<AccountDeletionCancellationTokenProvider>(
                    AccountDeletionCancellationTokenProvider.ProviderName)
                .AddTokenProvider<EmailChangeTokenProvider>(
                    EmailChangeTokenProvider.ProviderName)
                // AddTokenProvider checks for IUserTwoFactorTokenProvider<TUser>, not for
                // DataProtectorTokenProvider, so the hand-written revert provider registers the same
                // way the others do (ADR-0048).
                .AddTokenProvider<EmailChangeRevertTokenProvider>(
                    EmailChangeRevertTokenProvider.ProviderName)
                .AddEntityFrameworkStores<AuthDbContext>();

            services.AddOptions<EmailChangeTokenProviderOptions>();
            services.AddOptions<EmailChangeRevertTokenProviderOptions>();

            services.Configure<DataProtectionTokenProviderOptions>(options =>
            {
                options.TokenLifespan = TimeSpan.FromHours(24);
            });

            return services;
        }
    }
}
