using System.Text.Json.Serialization;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using LotroKoniecDev.Hateoas;
using LotroKoniecDev.Options;
using LotroKoniecDev.SharedKernel.Authorization;
using LotroKoniecDev.SharedKernel.Messaging;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.API.Auth;
using LotroKoniecDev.TranslationSystem.API.Auth.CurrentUserAccessing;
using LotroKoniecDev.TranslationSystem.API.Auth.Provisioning;
using LotroKoniecDev.TranslationSystem.API.ExceptionHandlers;
using LotroKoniecDev.TranslationSystem.API.Extensions;
using LotroKoniecDev.TranslationSystem.API.Features.GameVersions;
using LotroKoniecDev.TranslationSystem.API.Features.Import;
using LotroKoniecDev.TranslationSystem.API.Features.Progress;
using LotroKoniecDev.TranslationSystem.API.Features.TranslationFiles;
using LotroKoniecDev.TranslationSystem.API.Features.Translations;
using LotroKoniecDev.TranslationSystem.API.Features.Translators;
using LotroKoniecDev.TranslationSystem.API.Hateoas.DiscoveryFactories;
using LotroKoniecDev.TranslationSystem.API.Hateoas.GameVersionAggregateFactories;
using LotroKoniecDev.TranslationSystem.API.Hateoas.PaginationLinkFactories;
using LotroKoniecDev.TranslationSystem.API.Hateoas.TranslationAggregateFactories;
using LotroKoniecDev.TranslationSystem.API.Messaging;
using LotroKoniecDev.TranslationSystem.API.Parsing;
using LotroKoniecDev.TranslationSystem.Contracts.Common;
using LotroKoniecDev.TranslationSystem.Contracts.GameVersions;
using LotroKoniecDev.TranslationSystem.Contracts.Import;
using LotroKoniecDev.TranslationSystem.Contracts.Progress;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;
using LotroKoniecDev.TranslationSystem.Contracts.Translators;

namespace LotroKoniecDev.TranslationSystem.API;

internal static class ApiDependencyInjection
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddApi()
        {
            services.AddTransient<IDiscoveryLinkFactory, DiscoveryLinkFactory>();
            services.AddTransient<ITranslationAggregateLinkFactory, TranslationAggregateLinkFactory>();
            services.AddTransient<IGameVersionAggregateLinkFactory, GameVersionAggregateLinkFactory>();
            services.AddTransient<IPaginationLinkFactory, PaginationLinkFactory>();

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
            // After AddProblemDetails(), so ASP.NET Core's own writer keeps the plain JSON and RFC 7807
            // Accept values and ours only covers the vendor media type.
            services.AddHateoasInfrastructure();

            services.AddSingleton(TimeProvider.System);

            // In memory only, no IDistributedCache (PERF-07): the per-request translator lookup is small
            // and local, and a shared cache would only add latency.
            services.AddHybridCache(options =>
            {
                options.MaximumPayloadBytes = 1024 * 64;
                options.MaximumKeyLength = 256;
                options.DefaultEntryOptions = new HybridCacheEntryOptions
                {
                    Expiration = TimeSpan.FromMinutes(5),
                    LocalCacheExpiration = TimeSpan.FromMinutes(5)
                };
            });

            services.AddImportFeature();
            services.AddProgressFeature();
            services.AddTranslationsFeature();
            services.AddTranslatorsFeature();
            services.AddTranslationFilesFeature();
            services.AddGameVersionsFeature();

            return services;
        }

        private void AddImportFeature()
        {
            services.AddSingleton<ITranslationExportParser, TranslationExportParser>();
            services.AddOptions<ImportSettings>()
                .BindConfiguration(ImportSettings.ConfigurationSection)
                .Validate(
                    settings => settings.ApplyChunkSize >= 1,
                    $"{ImportSettings.ConfigurationSection}:{nameof(ImportSettings.ApplyChunkSize)} must be at least 1.")
                .ValidateOnStart();

            // The import is the only multipart endpoint, so raising the global limit is safe. Without it
            // the framework's 128 MB default would cut the upload limit short (spec 0003, #208).
            services.AddOptions<FormOptions>()
                .Configure<IOptions<ImportSettings>>((formOptions, importSettings) =>
                    formOptions.MultipartBodyLengthLimit = importSettings.Value.MaxUploadBytes);

            services.AddScoped<IValidator<ImportExportedTexts.Command>, ImportExportedTexts.Validator>();
            services.AddScoped<ICommandHandler<ImportExportedTexts.Command, Result<ImportSummary>>, ImportExportedTexts.Handler>();
        }

        private void AddProgressFeature()
        {
            services.AddScoped<
                IQueryHandler<GetPublicProgress.Query, Result<PublicProgressResponse>>,
                GetPublicProgress.Handler>();
        }

        private void AddTranslationsFeature()
        {
            services.AddScoped<
                IQueryHandler<ListTranslations.Query, Result<PaginationResponse<TranslationListItemResponse>>>,
                ListTranslations.Handler>();
            services.AddScoped<
                IQueryHandler<GetTranslation.Query, Result<GetTranslation.QueryResult>>,
                GetTranslation.Handler>();
            services.AddScoped<
                IQueryHandler<GetTranslationStats.Query, Result<TranslationStatsResponse>>,
                GetTranslationStats.Handler>();

            services.AddScoped<IValidator<UpsertTranslation.Command>, UpsertTranslation.Validator>();
            services.AddScoped<
                ICommandHandler<UpsertTranslation.Command, Result<TranslationDetailResponse>>,
                UpsertTranslation.Handler>();

            services.AddScoped<IValidator<ApproveTranslation.Command>, ApproveTranslation.Validator>();
            services.AddScoped<
                ICommandHandler<ApproveTranslation.Command, Result>,
                ApproveTranslation.Handler>();

            services.AddScoped<IValidator<BulkApproveTranslations.Command>, BulkApproveTranslations.Validator>();
            services.AddScoped<
                ICommandHandler<BulkApproveTranslations.Command, Result<BulkApproveTranslationsResponse>>,
                BulkApproveTranslations.Handler>();
        }

        private void AddTranslatorsFeature()
        {
            services.AddScoped<
                IQueryHandler<ExportMyContributionData.Query, Result<TranslatorDataExportResponse>>,
                ExportMyContributionData.Handler>();

            // The AuthSystem's account-erased events (ADR-0065).
            services.AddScoped<IValidator<EraseTranslatorProfile.Command>, EraseTranslatorProfile.Validator>();
            services.AddScoped<ICommandHandler<EraseTranslatorProfile.Command, Result>, EraseTranslatorProfile.Handler>();
            services.AddSingleton<IValidator<RabbitMqSettings>, RabbitMqSettingsValidator>();
            services.AddOptionsWithFluentValidation<RabbitMqSettings>(RabbitMqSettings.ConfigurationSection);
            services.AddHostedService<AccountErasedConsumer>();
        }

        private void AddTranslationFilesFeature()
        {
            // Singletons: no state apart from the projector's one-rebuild-at-a-time gate. The projector
            // opens its own scope for the EF services.
            services.AddSingleton<ITranslationFileSerializer, TranslationFileSerializer>();
            services.AddSingleton<IPrecomputedTranslationFileProjector, PrecomputedTranslationFileProjector>();

            // Debounced background rebuild (PERF-04, ADR-0021). The worker needs the scheduler's channel
            // reader, so the concrete type is the singleton and the interface points at it.
            services.AddOptions<TranslationFileRebuildSettings>()
                .BindConfiguration(TranslationFileRebuildSettings.ConfigurationSection)
                .Validate(
                    settings => settings.DebounceWindow >= TimeSpan.Zero
                        && settings.DebounceWindow <= TranslationFileRebuildSettings.MaxDebounceWindow,
                    $"{TranslationFileRebuildSettings.ConfigurationSection}:{nameof(TranslationFileRebuildSettings.DebounceWindow)} must be between 0 and {TranslationFileRebuildSettings.MaxDebounceWindow}.")
                .ValidateOnStart();
            services.AddSingleton<TranslationFileRebuildScheduler>();
            services.AddSingleton<ITranslationFileRebuildScheduler>(serviceProvider =>
                serviceProvider.GetRequiredService<TranslationFileRebuildScheduler>());
            services.AddHostedService<TranslationFileRebuildWorker>();

            // Rewrites an artifact stored before ADR-0047 added source_digest, or an updated CLI patches
            // nothing until the next approve ("Deploy ordering" in its Consequences).
            services.AddHostedService<TranslationFileFormatUpgradeService>();

            // PERF-09, ADR-0064. A singleton for its one-write-at-a-time gate; disposing it at shutdown
            // removes its private folder.
            services.AddOptions<TranslationFileDiskCacheSettings>()
                .BindConfiguration(TranslationFileDiskCacheSettings.ConfigurationSection)
                .Validate(
                    settings => settings.Directory is null || Path.IsPathFullyQualified(settings.Directory),
                    $"{TranslationFileDiskCacheSettings.ConfigurationSection}:{nameof(TranslationFileDiskCacheSettings.Directory)} must be an absolute path, or left out.")
                .ValidateOnStart();
            services.AddSingleton<ITranslationFileDiskCache, TranslationFileDiskCache>();

            services.AddScoped<
                IQueryHandler<GetTranslationFile.HashQuery, Result<string>>,
                GetTranslationFile.HashHandler>();
            services.AddScoped<
                IQueryHandler<GetTranslationFile.Query, Result<GetTranslationFile.TranslationFileResult>>,
                GetTranslationFile.Handler>();
        }

        private void AddGameVersionsFeature()
        {
            services.AddScoped<
                IQueryHandler<ListGameVersions.Query, Result<IReadOnlyList<GameVersionResponse>>>,
                ListGameVersions.Handler>();
            services.AddScoped<
                IQueryHandler<GetGameVersion.Query, Result<GameVersionResponse>>,
                GetGameVersion.Handler>();

            services.AddScoped<IValidator<RegisterGameVersion.Command>, RegisterGameVersion.Validator>();
            services.AddScoped<
                ICommandHandler<RegisterGameVersion.Command, Result<GameVersionResponse>>,
                RegisterGameVersion.Handler>();

            services.AddScoped<IValidator<DeleteGameVersion.Command>, DeleteGameVersion.Validator>();
            services.AddScoped<ICommandHandler<DeleteGameVersion.Command, Result>, DeleteGameVersion.Handler>();
        }

        public IServiceCollection AddJwtBearerAuthentication(IWebHostEnvironment environment)
        {
            services.AddSingleton<IValidator<AuthSettings>, AuthSettingsValidator>();
            services.AddOptionsWithFluentValidation<AuthSettings>(AuthSettings.ConfigurationSection);

            // ASP.NET Core's JwtBearer, not OpenIddict's validator (ADR-0067, decision 5). The two rules
            // OpenIddict has built in, no clock skew and access tokens only, are set by hand below (#933).
            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer();

            services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
                .Configure<IOptions<AuthSettings>>((options, authSettingsAccessor) =>
                {
                    AuthSettings settings = authSettingsAccessor.Value;

                    // Outside Development and Testing, an https issuer means https metadata too: the deployed
                    // stacks fetch it through the proxy (compose.hetzner.yaml), not over the internal network.
                    bool requireHttps = !environment.IsDevelopment()
                                        && !environment.IsTesting()
                                        && settings.Issuer.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

                    options.Authority = settings.EffectiveAuthority;
                    options.Audience = settings.Audience;
                    options.RequireHttpsMetadata = requireHttps;

                    // Keep the OpenIddict claim names (sub, name, role) unchanged.
                    options.MapInboundClaims = false;

                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = true,
                        ValidIssuer = settings.Issuer,
                        ValidateAudience = true,
                        ValidAudience = settings.Audience,
                        ValidateLifetime = true,
                        // The five-minute default would double the revocation window of ADR-0049, and
                        // the auth server and this API share one clock.
                        ClockSkew = TimeSpan.Zero,
                        // The auth server signs other tokens with this audience too, such as an ID
                        // token. Only an access token is a credential here (RFC 9068).
                        ValidTypes =
                        [
                            AuthConstants.TokenTypes.AccessToken,
                            AuthConstants.TokenTypes.AccessTokenMediaType
                        ],
                        ValidateIssuerSigningKey = true,
                        NameClaimType = "name",
                        RoleClaimType = "role"
                    };

                    // The library decodes these answers by the charset they name, so a name .NET cannot
                    // use is dropped first (#972). A back-channel handler set by a test is wrapped, not
                    // replaced.
                    options.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                        $"{settings.EffectiveAuthority.TrimEnd('/')}/.well-known/openid-configuration",
                        new OpenIdConnectConfigurationRetriever(),
                        new HttpDocumentRetriever(new HttpClient(new UnknownCharsetDelegatingHandler
                        {
                            InnerHandler = options.BackchannelHttpHandler ?? new HttpClientHandler()
                        }))
                        {
                            RequireHttps = requireHttps
                        });
                });

            services.AddAuthorizationPolicies();

            services.AddHttpContextAccessor();
            services.AddScoped<ICurrentUserAccessor, CurrentUserAccessor>();
            services.AddScoped<ITranslatorProvisioner, TranslatorProvisioner>();

            return services;
        }

        private void AddAuthorizationPolicies()
        {
            // Authorized by default (house rule): a public endpoint says AllowAnonymous.
            services.AddAuthorizationBuilder()
                .SetFallbackPolicy(new AuthorizationPolicyBuilder()
                    .RequireAuthenticatedUser()
                    .Build())
                .AddPolicy(AuthorizationPolicies.RequireAuthenticatedUser, policy =>
                    policy.RequireAuthenticatedUser())
                .AddPolicy(AuthorizationPolicies.RequireAdminRole, policy =>
                    policy.RequireRole(AuthConstants.Roles.Admin))
                .AddPolicy(AuthorizationPolicies.RequireTranslatorRole, policy =>
                    policy.RequireRole(AuthConstants.Roles.Admin, AuthConstants.Roles.Translator))
                .AddPolicy(AuthorizationPolicies.ApiScope, policy =>
                    policy.RequireAssertion(context =>
                        HasScope(context.User, AuthConstants.Scopes.Api)))
                .AddPolicy(AuthorizationPolicies.RequireServiceScope, policy =>
                    policy.RequireAssertion(context =>
                        HasScope(context.User, AuthConstants.Scopes.Service)));
        }

        private static bool HasScope(System.Security.Claims.ClaimsPrincipal user, string requiredScope)
        {
            // Scopes come space-separated in one claim (the standard) or one claim per scope.
            IEnumerable<System.Security.Claims.Claim> scopeClaims = user.FindAll("scope");

            foreach (System.Security.Claims.Claim claim in scopeClaims)
            {
                string[] scopes = claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (scopes.Contains(requiredScope))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
