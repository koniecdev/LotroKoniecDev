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
            // Registers ILinkFactory, IHttpContextAccessor, the fallback IProblemDetailsWriter for our
            // vendor media type, and the JsonTypeInfo modifier that hides empty 'links' arrays in plain
            // JSON. It has to come after AddProblemDetails(), so ASP.NET Core's own writer keeps the
            // plain JSON and RFC 7807 Accept values.
            services.AddHateoasInfrastructure();

            // The clock every command handler that writes a timestamp uses, such as the import diff and
            // the translation upsert. It is registered once here and not per feature.
            services.AddSingleton(TimeProvider.System);

            // An in-memory HybridCache only (PERF-07). No IDistributedCache is registered, so it uses
            // its in-memory store. The translator lookup that runs on every authenticated request is
            // small and local to this process, and a shared cache would only add latency.
            // This mirrors TheKittySaver's AuthSystem HybridCache setup. The short TTL is the default
            // here and is written out again where provisioning uses it.
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

            // The import is the only multipart endpoint, so raising the global multipart limit to the
            // configured upload size is safe and keeps it in step with the endpoint's own request-body
            // limit. Without it the framework's 128 MB default would cut a larger limit short
            // (spec 0003, #208).
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
        }

        private void AddTranslationFilesFeature()
        {
            // The serializer and the projector hold no state, apart from the projector's gate that lets
            // one rebuild run at a time, so both are singletons. The projector opens its own scope to
            // resolve the scoped EF services.
            services.AddSingleton<ITranslationFileSerializer, TranslationFileSerializer>();
            services.AddSingleton<IPrecomputedTranslationFileProjector, PrecomputedTranslationFileProjector>();

            // The background rebuild waits a moment before it runs (PERF-04, ADR-0021). Write handlers
            // signal the scheduler, and the worker collects those signals into one rebuild and runs the
            // projector for as long as the host lives. The worker needs the scheduler's channel reader,
            // which is why the concrete type is registered as a singleton and the interface points at
            // it.
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

            // A one-off catch-up for a stored artifact written before ADR-0047 added the source_digest
            // column (see "Deploy ordering" in its Consequences). Without it an updated CLI patches
            // nothing until the next approve rebuilds the artifact.
            services.AddHostedService<TranslationFileFormatUpgradeService>();

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

            // Plain JWT Bearer authentication against the AuthSystem (OpenIddict) issuer. It avoids
            // OpenIddict's own scope permission checks, which are hard to configure.
            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer();

            services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
                .Configure<IOptions<AuthSettings>>((options, authSettingsAccessor) =>
                {
                    AuthSettings settings = authSettingsAccessor.Value;

                    // In containers the services talk to each other over plain HTTP. HTTPS ends at the
                    // ingress or load balancer.
                    bool requireHttps = !environment.IsDevelopment()
                                        && !environment.IsTesting()
                                        && settings.Issuer.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

                    options.Authority = settings.EffectiveAuthority;
                    options.Audience = settings.Audience;
                    options.RequireHttpsMetadata = requireHttps;

                    // Turn off the default claim renaming, so the OpenIddict claim names (sub, name,
                    // role) reach the ClaimsPrincipal unchanged.
                    options.MapInboundClaims = false;

                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = true,
                        ValidIssuer = settings.Issuer,
                        ValidateAudience = true,
                        ValidAudience = settings.Audience,
                        ValidateLifetime = true,
                        ValidateIssuerSigningKey = true,
                        NameClaimType = "name",
                        RoleClaimType = "role"
                    };

                    // Read the signing keys from the JWKS endpoint.
                    options.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                        $"{settings.EffectiveAuthority.TrimEnd('/')}/.well-known/openid-configuration",
                        new OpenIdConnectConfigurationRetriever(),
                        new HttpDocumentRetriever { RequireHttps = requireHttps });
                });

            services.AddAuthorizationPolicies();

            services.AddHttpContextAccessor();
            services.AddScoped<ICurrentUserAccessor, CurrentUserAccessor>();
            services.AddScoped<ITranslatorProvisioner, TranslatorProvisioner>();

            return services;
        }

        private void AddAuthorizationPolicies()
        {
            // Endpoints require a logged-in user by default (house rule): any endpoint without its own
            // authorization metadata is closed. A public endpoint says so with AllowAnonymous, as the
            // health checks do.
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
            // OAuth2 scopes arrive in one of two shapes:
            // 1. Several scopes in one "scope" claim, separated by spaces, which is the standard.
            // 2. One "scope" claim per scope, which some identity providers send.
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
