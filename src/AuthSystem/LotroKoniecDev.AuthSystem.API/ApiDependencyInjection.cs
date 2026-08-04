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

            // Razor renders Polish diacritics (ł, ż, ó, …) as numeric HTML entities
            // unless the encoder is widened. Browsers handle entities fine, but the
            // raw HTML becomes hard to read and breaks string-based assertions.
            services.Configure<WebEncoderOptions>(options =>
            {
                options.TextEncoderSettings = new TextEncoderSettings(UnicodeRanges.All);
            });
            // Registers ILinkFactory, IHttpContextAccessor, the fallback IProblemDetailsWriter
            // for the HATEOAS vendor media type, and the JsonTypeInfo modifier that strips
            // empty 'links' arrays from plain-JSON responses. Must follow AddProblemDetails()
            // so ASP.NET Core's default writer handles plain JSON / RFC 7807 Accept values first.
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

            // Signal-driven outbox relay (ADR-0035): outbox writers stage rows through the shared
            // writer and nudge the singleton signal after their commit instead of the relay
            // polling the database on an interval.
            services.AddSingleton<OutboxSignal>();
            services.AddScoped<OutboxWriter>();
            services.AddHostedService<OutboxRelay>();

            // The consuming side of the same pipeline: broker deliveries -> e-mails. The keyed
            // registrations ARE the processor registry (ADR-0038): one line per message type,
            // keyed by the outbox row's Type — an explicit, compile-visible inventory (ADR-0001,
            // no assembly scanning). Processors are scoped because the consumer resolves them per
            // message, mirroring how a request would; the pump itself is a singleton hosted
            // service.
            services.AddKeyedScoped<IEmailMessageProcessor, EmailConfirmationRequestProcessor>(
                nameof(EmailConfirmationRequested));
            services.AddKeyedScoped<IEmailMessageProcessor, PasswordResetRequestProcessor>(
                nameof(PasswordResetRequested));
            services.AddScoped<EmailDeliveryProcessor>();
            services.AddHostedService<EmailDispatchConsumer>();

            // PERF-02: reference refresh tokens accumulate one row per refresh and are never
            // deleted otherwise; prune expired/invalid tokens and authorizations daily.
            services.AddHostedService<OpenIddictPruneService>();

            // Fail-fast startup validation of the OpenIddict server config (ADR-0008 §3, M6-05): the
            // OpenIddictSettingsValidator enforces the production key material / issuer and names the
            // offending key + environment. No ValidateDataAnnotations() — the settings carry no
            // DataAnnotations attributes (the `required` issuer is a binder constraint), so that call
            // validated nothing.
            services.AddOptions<OpenIddictSettings>()
                .BindConfiguration(OpenIddictSettings.ConfigurationSection)
                .ValidateOnStart();

            services.AddSingleton<IValidateOptions<OpenIddictSettings>, OpenIddictSettingsValidator>();

            services.AddOptions<GdprSettings>()
                .BindConfiguration(GdprSettings.ConfigurationSection)
                .ValidateOnStart();

            services.AddSingleton<IValidateOptions<GdprSettings>, GdprSettingsValidator>();

            // The cancel-deletion token must stay valid for the whole grace period,
            // unlike the 24h default token lifespan.
            services.AddOptions<AccountDeletionCancellationTokenProviderOptions>()
                .Configure<IOptions<GdprSettings>>((options, gdprSettings) =>
                {
                    options.TokenLifespan = gdprSettings.Value.DeletionGracePeriod;
                });

            return services;
        }
    }
}
