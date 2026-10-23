using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.WebEncoders;
using LotroKoniecDev.AuthSystem.API.BackgroundServices;
using LotroKoniecDev.AuthSystem.API.Common;
using LotroKoniecDev.AuthSystem.API.ExceptionHandlers;
using LotroKoniecDev.AuthSystem.API.Extensions;
using LotroKoniecDev.AuthSystem.API.Features.Auth;
using LotroKoniecDev.AuthSystem.API.Hateoas.AccountAggregateFactories;
using LotroKoniecDev.AuthSystem.API.Hateoas.DiscoveryFactories;
using LotroKoniecDev.AuthSystem.API.Middleware;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.API.Pages.Account;
using LotroKoniecDev.AuthSystem.API.Services.Accounts;
using LotroKoniecDev.AuthSystem.API.Services.Emails;
using LotroKoniecDev.AuthSystem.API.Services.Emails.Templates;
using LotroKoniecDev.AuthSystem.API.Services.Gdpr;
using LotroKoniecDev.AuthSystem.API.Services.Maintenance;
using LotroKoniecDev.AuthSystem.API.Services.RateLimiting;
using LotroKoniecDev.AuthSystem.API.Services.ResponseTiming;
using LotroKoniecDev.AuthSystem.API.Services.Sessions;
using LotroKoniecDev.AuthSystem.API.Settings;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.Identity;
using LotroKoniecDev.Hateoas;
using LotroKoniecDev.SharedKernel.Messaging;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;

namespace LotroKoniecDev.AuthSystem.API;

internal static class ApiDependencyInjection
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddAuthApi(IWebHostEnvironment environment)
        {
            services.AddOpenIddictServer(environment);

            services.AddExceptionHandler<BadHttpRequestExceptionHandler>();
            services.AddExceptionHandler<FluentValidationExceptionHandler>();
            services.AddExceptionHandler<ArgumentExceptionHandler>();
            services.AddExceptionHandler<DbUpdateConcurrencyExceptionHandler>();
            services.AddExceptionHandler<GlobalExceptionHandler>();

            // Must stay before AddProblemDetails(); BrowserErrorPageWriter says why.
            services.AddSingleton<IProblemDetailsWriter, BrowserErrorPageWriter>();
            services.AddProblemDetails(options => options.CustomizeProblemDetails = OpenIddictErrorProblemDetails.Add);

            services.ConfigureHttpJsonOptions(jsonOptions =>
            {
                jsonOptions.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
            });

            // Without it, Razor writes Polish letters such as ł as numeric HTML entities, which breaks
            // tests that compare strings.
            services.Configure<WebEncoderOptions>(options =>
            {
                options.TextEncoderSettings = new TextEncoderSettings(UnicodeRanges.All);
            });
            // After AddProblemDetails(), so ASP.NET Core's own writer keeps the plain JSON and RFC 7807
            // Accept values and ours only handles the vendor media type.
            services.AddHateoasInfrastructure();

            services.AddTransient<IAccountAggregateLinkFactory, AccountAggregateLinkFactory>();
            services.AddTransient<IDiscoveryLinkFactory, DiscoveryLinkFactory>();

            services.AddEndpoints(Assembly.GetExecutingAssembly());

            services.AddValidatorsFromAssembly(Assembly.GetExecutingAssembly(), includeInternalTypes: true);

            services.AddScoped<ICommandHandler<CancelAccountDeletion.Command, Result<CancelAccountDeletion.CancelledDeletion>>, CancelAccountDeletion.Handler>();
            services.AddScoped<ICommandHandler<ChangePassword.Command, Result>, ChangePassword.Handler>();
            services.AddScoped<ICommandHandler<ConfirmEmail.Command, Result>, ConfirmEmail.Handler>();
            services.AddScoped<ICommandHandler<ConfirmEmailChange.Command, Result>, ConfirmEmailChange.Handler>();
            services.AddScoped<ICommandHandler<DeleteAccount.Command, Result<DeleteAccount.ScheduledDeletion>>, DeleteAccount.Handler>();
            services.AddScoped<IQueryHandler<DownloadAccountData.Query, Result<AccountDataExportResponse>>, DownloadAccountData.Handler>();
            services.AddScoped<IQueryHandler<ExportAccountData.Query, Result<AccountDataExportResponse>>, ExportAccountData.Handler>();
            services.AddScoped<ICommandHandler<ForgotPassword.Command, Result>, ForgotPassword.Handler>();
            services.AddScoped<ICommandHandler<RegisterUser.Command, Result>, RegisterUser.Handler>();
            services.AddScoped<ICommandHandler<RequestEmailChange.Command, Result>, RequestEmailChange.Handler>();
            services.AddScoped<ICommandHandler<RevertEmailChange.Command, Result<RevertEmailChange.RevertedEmailChange>>, RevertEmailChange.Handler>();
            services.AddScoped<ICommandHandler<ResendEmailConfirmation.Command, Result>, ResendEmailConfirmation.Handler>();
            services.AddScoped<ICommandHandler<ResetPassword.Command, Result>, ResetPassword.Handler>();

            services.AddScoped<IEmailVerificationLinkFactory, EmailVerificationLinkFactory>();
            services.AddScoped<IPasswordResetLinkFactory, PasswordResetLinkFactory>();
            services.AddScoped<ICancelDeletionLinkFactory, CancelDeletionLinkFactory>();
            services.AddScoped<EmailChangeLinkFactory>();
            services.AddScoped<IEmailChangeVerificationLinkFactory>(sp => sp.GetRequiredService<EmailChangeLinkFactory>());
            services.AddScoped<IEmailChangeRevertLinkFactory>(sp => sp.GetRequiredService<EmailChangeLinkFactory>());
            services.AddSingleton<UsedLinkNextStepCookie>();
            services.AddSingleton<IEmailTemplateRenderer, EmailTemplateRenderer>();
            services.AddSingleton<FrontendHome>();
            services.AddScoped<IPasswordResetEmailSender, PasswordResetEmailSender>();
            services.AddScoped<IAccountConfirmationEmailSender, AccountConfirmationEmailSender>();
            services.AddScoped<IAccountDeletionEmailSender, AccountDeletionEmailSender>();
            services.AddScoped<IEmailChangeEmailSender, EmailChangeEmailSender>();

            services.AddScoped<IAccountErasureService, AccountErasureService>();
            services.AddScoped<IErasedAccountReconciler, ErasedAccountReconciler>();
            services.AddScoped<IAccountDeletionSchedule, AccountDeletionSchedule>();
            services.AddScoped<IAccountDeletionFinalizer, AccountDeletionFinalizer>();
            services.AddHostedService<AccountDeletionFinalizerHostedService>();

            // After AddIdentityCore, which uses TryAdd for the built-in validator: added first, this one
            // would replace Identity's rules instead of joining them (ADR-0066).
            services.AddScoped<IPasswordValidator<ApplicationUser>, BreachedPasswordValidator>();

            services.AddScoped<IUserSessionRevoker, UserSessionRevoker>();
            services.AddSingleton<SignInSessionTicketStore>();
            services.AddScoped<IEmailChangeRevertWindow, EmailChangeRevertWindow>();
            services.AddScoped<IEmailChangeRevertReservation, EmailChangeRevertReservation>();

            // Singletons, because every request shares a budget. These count per account or per inbox,
            // which a limiter policy cannot do: it runs before the account is known. One interface per
            // budget, so a handler cannot spend the wrong one (#813, ADR-0057).
            services.AddSingleton<IPasswordResetRequestThrottle>(_ =>
                new PasswordResetRequestThrottle(AccountBudgets.PasswordResetPermitLimit, AccountBudgets.Window));
            services.AddSingleton<IPasswordConfirmationThrottle>(_ =>
                new PerAccountFixedWindowThrottle(AccountBudgets.PasswordConfirmationPermitLimit, AccountBudgets.Window));
            services.AddSingleton<IAccountDeletionScheduleThrottle>(_ =>
                new PerAccountFixedWindowThrottle(AccountBudgets.DeletionSchedulePermitLimit, AccountBudgets.DeletionScheduleWindow));
            services.AddSingleton<IDeletionScheduledLoginThrottle>(_ =>
                new PerAccountFixedWindowThrottle(AccountBudgets.DeletionScheduledLoginPermitLimit, AccountBudgets.Window));
            services.AddSingleton<IEmailConfirmationResendThrottle>(_ =>
                new PerMailboxFixedWindowThrottle(AccountBudgets.EmailConfirmationResendPermitLimit, AccountBudgets.Window));
            services.AddSingleton<IEmailChangeRecipientThrottle>(_ =>
                new PerMailboxFixedWindowThrottle(AccountBudgets.EmailChangeRecipientPermitLimit, AccountBudgets.Window));
            services.AddSingleton<IRegistrationMailboxThrottle>(_ =>
                new PerMailboxFixedWindowThrottle(AccountBudgets.RegistrationPermitLimit, AccountBudgets.Window));

            // Every answer that hides whether an address has an account waits for this floor (ADR-0059).
            services.AddSingleton<IResponseTimeFloor, ResponseTimeFloor>();

            // The frontend's caller key, required outside Development and Testing (ADR-0054).
            services.AddOptions<FrontendCallerSettings>()
                .BindConfiguration(FrontendCallerSettings.ConfigurationSection)
                .ValidateOnStart();
            services.AddSingleton<IValidateOptions<FrontendCallerSettings>, FrontendCallerSettingsValidator>();
            services.AddSingleton<RateLimitPartitionKeyResolver>();

            // The full /health runs only for a caller with this key, required outside Development and
            // Testing (ADR-0058).
            services.AddOptions<HealthCheckSettings>()
                .BindConfiguration(HealthCheckSettings.ConfigurationSection)
                .ValidateOnStart();
            services.AddSingleton<IValidateOptions<HealthCheckSettings>, HealthCheckSettingsValidator>();

            // The interceptor wakes the relay after a commit that carries outbox rows (ADR-0035, ADR-0038
            // §6); AddAuthPersistence puts every IInterceptor on the AuthDbContext. Scoped, because it
            // keeps state between a save and the commit that follows.
            services.AddSingleton<OutboxSignal>();
            services.AddScoped<OutboxWriter>();
            services.AddScoped<IInterceptor, OutboxWakeUpInterceptor>();
            services.AddHostedService<OutboxRelay>();

            // The processor registry, one line per message type keyed by the outbox row's Type (ADR-0038),
            // written out so nothing scans assemblies. Scoped, because the consumer resolves one per message.
            services.AddKeyedScoped<IEmailMessageProcessor, EmailConfirmationRequestProcessor>(
                nameof(EmailConfirmationRequested));
            services.AddKeyedScoped<IEmailMessageProcessor, RegistrationAttemptedProcessor>(
                nameof(RegistrationAttemptedOnExistingAccount));
            services.AddKeyedScoped<IEmailMessageProcessor, PasswordResetRequestProcessor>(
                nameof(PasswordResetRequested));
            services.AddKeyedScoped<IEmailMessageProcessor, AccountDeletionScheduledProcessor>(
                nameof(AccountDeletionScheduled));
            services.AddKeyedScoped<IEmailMessageProcessor, AccountDeletionCancelledProcessor>(
                nameof(AccountDeletionCancelled));
            services.AddKeyedScoped<IEmailMessageProcessor, EmailChangeRequestProcessor>(
                nameof(EmailChangeRequested));
            services.AddKeyedScoped<IEmailMessageProcessor, EmailChangeCompletedProcessor>(
                nameof(EmailChangeCompleted));
            services.AddScoped<EmailDeliveryProcessor>();
            services.AddHostedService<EmailDispatchConsumer>();

            // Reference refresh tokens add a row per refresh and nothing else deletes them (PERF-02).
            services.AddHostedService<OpenIddictPruneService>();

            // ADR-0062: expired sign-in sessions whose cookie never came back.
            services.AddHostedService<SignInSessionPruneService>();

            // OpenIddictSettingsValidator decides which values each environment must have (ADR-0008 §3).
            services.AddOptions<OpenIddictSettings>()
                .BindConfiguration(OpenIddictSettings.ConfigurationSection)
                .ValidateOnStart();

            services.AddSingleton<IValidateOptions<OpenIddictSettings>, OpenIddictSettingsValidator>();

            services.AddOptions<GdprSettings>()
                .BindConfiguration(GdprSettings.ConfigurationSection)
                .ValidateOnStart();

            services.AddSingleton<IValidateOptions<GdprSettings>, GdprSettingsValidator>();

            // The cancel-deletion link has to work for the whole grace period, not the default 24 hours.
            services.AddOptions<AccountDeletionCancellationTokenProviderOptions>()
                .Configure<IOptions<GdprSettings>>((options, gdprSettings) =>
                {
                    options.TokenLifespan = gdprSettings.Value.DeletionGracePeriod;
                });

            return services;
        }
    }
}
