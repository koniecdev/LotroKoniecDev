using System.Text.Json.Serialization;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
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
using LotroKoniecDev.TranslationSystem.API.ExceptionHandlers;
using LotroKoniecDev.TranslationSystem.API.Extensions;
using LotroKoniecDev.TranslationSystem.API.Features.Import;
using LotroKoniecDev.TranslationSystem.API.Features.TranslationFiles;
using LotroKoniecDev.TranslationSystem.API.Features.Translations;
using LotroKoniecDev.TranslationSystem.API.Hateoas.DiscoveryFactories;
using LotroKoniecDev.TranslationSystem.API.Parsing;
using LotroKoniecDev.TranslationSystem.Contracts.Common;
using LotroKoniecDev.TranslationSystem.Contracts.Import;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;

namespace LotroKoniecDev.TranslationSystem.API;

internal static class ApiDependencyInjection
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddApi()
        {
            services.AddTransient<IDiscoveryLinkFactory, DiscoveryLinkFactory>();

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
            // Registers ILinkFactory, IHttpContextAccessor, the fallback IProblemDetailsWriter
            // for the HATEOAS vendor media type, and the JsonTypeInfo modifier that strips
            // empty 'links' arrays from plain-JSON responses. Must follow AddProblemDetails()
            // so ASP.NET Core's default writer handles plain JSON / RFC 7807 Accept values first.
            services.AddHateoasInfrastructure();

            services.AddImportFeature();
            services.AddTranslationsFeature();
            services.AddTranslationFilesFeature();

            return services;
        }

        private void AddImportFeature()
        {
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton<ITranslationExportParser, TranslationExportParser>();
            services.AddOptions<ImportSettings>().BindConfiguration(ImportSettings.ConfigurationSection);

            services.AddScoped<IValidator<ImportExportedTexts.Command>, ImportExportedTexts.Validator>();
            services.AddScoped<ICommandHandler<ImportExportedTexts.Command, Result<ImportSummary>>, ImportExportedTexts.Handler>();
        }

        private void AddTranslationsFeature()
        {
            services.AddScoped<
                IQueryHandler<ListTranslations.Query, Result<PaginationResponse<TranslationListItemResponse>>>,
                ListTranslations.Handler>();
            services.AddScoped<
                IQueryHandler<GetTranslation.Query, Result<TranslationDetailResponse>>,
                GetTranslation.Handler>();
        }

        private void AddTranslationFilesFeature()
        {
            // Serializer + builder are stateless except the builder's single-flight gate, so both
            // are singletons; the builder resolves scoped EF services through a fresh scope.
            services.AddSingleton<ITranslationFileSerializer, TranslationFileSerializer>();
            services.AddSingleton<ITranslationArtifactBuilder, TranslationArtifactBuilder>();
            services.AddScoped<
                IQueryHandler<GetTranslationFile.Query, Result<GetTranslationFile.TranslationFileResult>>,
                GetTranslationFile.Handler>();
        }

        public IServiceCollection AddJwtBearerAuthentication(IWebHostEnvironment environment)
        {
            services.AddSingleton<IValidator<AuthSettings>, AuthSettingsValidator>();
            services.AddOptionsWithFluentValidation<AuthSettings>(AuthSettings.ConfigurationSection);

            // Standard JWT Bearer authentication against the AuthSystem (OpenIddict) issuer —
            // avoids OpenIddict's scope permission validation which is hard to configure.
            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer();

            services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
                .Configure<IOptions<AuthSettings>>((options, authSettingsAccessor) =>
                {
                    AuthSettings settings = authSettingsAccessor.Value;

                    // In containerized environments, internal service-to-service communication uses HTTP.
                    // HTTPS is terminated at the ingress/load balancer level.
                    bool requireHttps = !environment.IsDevelopment()
                                        && !environment.IsTesting()
                                        && settings.Issuer.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

                    options.Authority = settings.EffectiveAuthority;
                    options.Audience = settings.Audience;
                    options.RequireHttpsMetadata = requireHttps;

                    // Disable default claim type mapping so OpenIddict claim types
                    // (sub, name, role) are preserved as-is in the ClaimsPrincipal.
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

                    // Fetch signing keys from JWKS endpoint
                    options.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                        $"{settings.EffectiveAuthority.TrimEnd('/')}/.well-known/openid-configuration",
                        new OpenIdConnectConfigurationRetriever(),
                        new HttpDocumentRetriever { RequireHttps = requireHttps });
                });

            services.AddAuthorizationPolicies();

            services.AddHttpContextAccessor();
            services.AddScoped<ICurrentUserAccessor, CurrentUserAccessor>();

            return services;
        }

        private void AddAuthorizationPolicies()
        {
            // Endpoints are authorized by default (house rule): any endpoint without explicit
            // authorization metadata requires an authenticated user. Public endpoints opt out
            // explicitly with AllowAnonymous (health checks, future export download).
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
            // OAuth2 scopes can be:
            // 1. Space-separated in a single "scope" claim (OAuth2 standard)
            // 2. Multiple individual "scope" claims (some identity providers)
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
