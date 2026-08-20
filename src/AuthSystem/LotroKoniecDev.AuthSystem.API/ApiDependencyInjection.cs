using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using FluentValidation;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.WebEncoders;
using LotroKoniecDev.AuthSystem.API.BackgroundServices;
using LotroKoniecDev.AuthSystem.API.ExceptionHandlers;
using LotroKoniecDev.AuthSystem.API.Extensions;
using LotroKoniecDev.AuthSystem.API.Features.Auth;
using LotroKoniecDev.AuthSystem.API.Hateoas.AccountAggregateFactories;
using LotroKoniecDev.AuthSystem.API.Hateoas.DiscoveryFactories;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.API.Services.Emails;
using LotroKoniecDev.AuthSystem.API.Services.Emails.Templates;
using LotroKoniecDev.AuthSystem.API.Services.Gdpr;
using LotroKoniecDev.AuthSystem.API.Services.Maintenance;
using LotroKoniecDev.AuthSystem.API.Services.Sessions;
using LotroKoniecDev.AuthSystem.API.Settings;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
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
            services.AddProblemDetails();

            services.ConfigureHttpJsonOptions(jsonOptions =>
            {
                jsonOptions.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
            });

            // Without a wider encoder, Razor writes Polish letters such as ł, ż and ó as numeric HTML
            // entities. Browsers cope with that, but the raw HTML is hard to read and tests that
            // compare strings break.
            services.Configure<WebEncoderOptions>(options =>
            {
                options.TextEncoderSettings = new TextEncoderSettings(UnicodeRanges.All);
            });
            // Registers ILinkFactory, IHttpContextAccessor, the fallback IProblemDetailsWriter for our
            // vendor media type, and the JsonTypeInfo modifier that hides empty 'links' arrays in plain
            // JSON. It has to come after AddProblemDetails(), so ASP.NET Core's own writer keeps the
            // plain JSON and RFC 7807 Accept values.
            services.AddHateoasInfrastructure();

            services.AddTransient<IAccountAggregateLinkFactory, AccountAggregateLinkFactory>();
            services.AddTransient<IDiscoveryLinkFactory, DiscoveryLinkFactory>();

            services.AddEndpoints(Assembly.GetExecutingAssembly());

            services.AddValidatorsFromAssembly(Assembly.GetExecutingAssembly(), includeInternalTypes: true);

            services.AddScoped<ICommandHandler<CancelAccountDeletion.Command, Result<CancelAccountDeletion.CancelledDeletion>>, CancelAccountDeletion.Handler>();
            services.AddScoped<ICommandHandler<ChangePassword.Command, Result>, ChangePassword.Handler>();
            services.AddScoped<ICommandHandler<ConfirmEmail.Command, Result>, ConfirmEmail.Handler>();
            services.AddScoped<ICommandHandler<DeleteAccount.Command, Result<DeleteAccount.ScheduledDeletion>>, DeleteAccount.Handler>();
            services.AddScoped<IQueryHandler<ExportAccountData.Query, Result<AccountDataExportResponse>>, ExportAccountData.Handler>();
            services.AddScoped<ICommandHandler<ForgotPassword.Command, Result>, ForgotPassword.Handler>();
            services.AddScoped<ICommandHandler<RegisterUser.Command, Result<IdentityId>>, RegisterUser.Handler>();
            services.AddScoped<ICommandHandler<ResendEmailConfirmation.Command, Result>, ResendEmailConfirmation.Handler>();
            services.AddScoped<ICommandHandler<ResetPassword.Command, Result>, ResetPassword.Handler>();

            services.AddScoped<IEmailVerificationLinkFactory, EmailVerificationLinkFactory>();
            services.AddScoped<IPasswordResetLinkFactory, PasswordResetLinkFactory>();
            services.AddScoped<ICancelDeletionLinkFactory, CancelDeletionLinkFactory>();
            services.AddSingleton<IEmailTemplateRenderer, EmailTemplateRenderer>();
            services.AddScoped<IPasswordResetEmailSender, PasswordResetEmailSender>();
            services.AddScoped<IAccountConfirmationEmailSender, AccountConfirmationEmailSender>();
            services.AddScoped<IAccountDeletionEmailSender, AccountDeletionEmailSender>();

            services.AddScoped<IAccountErasureService, AccountErasureService>();
            services.AddScoped<IAccountDeletionFinalizer, AccountDeletionFinalizer>();
            services.AddHostedService<AccountDeletionFinalizerHostedService>();

            services.AddScoped<IUserSessionRevoker, UserSessionRevoker>();

            // The outbox relay works on a signal (ADR-0035). Writers add rows through the shared writer
            // and wake the singleton signal after their commit, so the relay does not poll the database
            // on a timer.
            services.AddSingleton<OutboxSignal>();
            services.AddScoped<OutboxWriter>();
            services.AddHostedService<OutboxRelay>();

            // The other end of the same pipeline: broker deliveries become e-mails. These keyed
            // registrations are the processor registry (ADR-0038), one line per message type, keyed by
            // the outbox row's Type. The list is written out here and visible to the compiler, so
            // nothing has to scan assemblies (ADR-0001).
            // The processors are scoped because the consumer resolves one per message, the way a
            // request would. The consumer itself is a singleton hosted service.
            services.AddKeyedScoped<IEmailMessageProcessor, EmailConfirmationRequestProcessor>(
                nameof(EmailConfirmationRequested));
            services.AddKeyedScoped<IEmailMessageProcessor, PasswordResetRequestProcessor>(
                nameof(PasswordResetRequested));
            services.AddKeyedScoped<IEmailMessageProcessor, AccountDeletionScheduledProcessor>(
                nameof(AccountDeletionScheduled));
            services.AddKeyedScoped<IEmailMessageProcessor, AccountDeletionCancelledProcessor>(
                nameof(AccountDeletionCancelled));
            services.AddScoped<EmailDeliveryProcessor>();
            services.AddHostedService<EmailDispatchConsumer>();

            // PERF-02: reference refresh tokens add one row per refresh and nothing else deletes them,
            // so expired and invalid tokens and authorizations are cleaned up once a day.
            services.AddHostedService<OpenIddictPruneService>();

            // Checks the OpenIddict server config at startup and stops the app when it is wrong
            // (ADR-0008 §3, M6-05). OpenIddictSettingsValidator requires real keys and an issuer in
            // production and names the key and environment at fault.
            // There is no ValidateDataAnnotations() call: the settings carry no DataAnnotations
            // attributes, since the `required` issuer is enforced by the binder, so that call checked
            // nothing.
            services.AddOptions<OpenIddictSettings>()
                .BindConfiguration(OpenIddictSettings.ConfigurationSection)
                .ValidateOnStart();

            services.AddSingleton<IValidateOptions<OpenIddictSettings>, OpenIddictSettingsValidator>();

            services.AddOptions<GdprSettings>()
                .BindConfiguration(GdprSettings.ConfigurationSection)
                .ValidateOnStart();

            services.AddSingleton<IValidateOptions<GdprSettings>, GdprSettingsValidator>();

            // The cancel-deletion token has to stay valid for the whole grace period, not for the
            // default 24 hours.
            services.AddOptions<AccountDeletionCancellationTokenProviderOptions>()
                .Configure<IOptions<GdprSettings>>((options, gdprSettings) =>
                {
                    options.TokenLifespan = gdprSettings.Value.DeletionGracePeriod;
                });

            return services;
        }
    }
}
