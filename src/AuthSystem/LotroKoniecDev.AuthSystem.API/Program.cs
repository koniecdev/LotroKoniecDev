using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Scalar.AspNetCore;
using Serilog;
using Serilog.Sinks.OpenTelemetry;
using LotroKoniecDev.AuthSystem.API;
using LotroKoniecDev.AuthSystem.API.Common;
using LotroKoniecDev.AuthSystem.API.Extensions;
using LotroKoniecDev.AuthSystem.API.Health;
using LotroKoniecDev.AuthSystem.API.Middleware;
using LotroKoniecDev.AuthSystem.API.Services.RateLimiting;
using LotroKoniecDev.AuthSystem.API.Services.Sessions;
using LotroKoniecDev.AuthSystem.API.Settings;
using LotroKoniecDev.AuthSystem.Persistence.Settings;
using LotroKoniecDev.Logging.Redaction;

// Not CreateBootstrapLogger(): it lives in the static Log.Logger, and the integration tests start
// several hosts from this Program at once, so the second host fails with "The logger is already
// frozen". In production the host's own logger still replaces this one.
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
    .CreateLogger();

try
{
    Log.Information("Starting AuthSystem");

    WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

    // DI validation in every environment (#572), so a broken graph that only appears under production
    // config fails at startup, before CD moves traffic, not as a 500. A forgotten handler registration
    // is still caught only by the endpoint integration tests.
    builder.Host.UseDefaultServiceProvider(static (_, options) =>
    {
        options.ValidateScopes = true;
        options.ValidateOnBuild = true;
    });

    // Optional git-ignored per-developer overrides, such as the AdminUser:* seed credentials. It
    // survives `docker compose down -v`, so a local admin is created again on the next start.
    builder.Configuration.AddJsonFile(
        "appsettings.Local.json",
        optional: true,
        reloadOnChange: true);

    string? otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
    bool otlpExportEnabled = !string.IsNullOrWhiteSpace(otlpEndpoint);

    // Logs reach Loki only through the box agent tailing stdout (ADR-0050 §7), which also catches a
    // crash before the logging pipeline is up. So the OTLP log sink stays off even when the endpoint is
    // set, unless OTEL_LOGS_EXPORTER=otlp asks for it on a stack with no tailer.
    bool otlpLogExportEnabled = otlpExportEnabled && string.Equals(
        builder.Configuration["OTEL_LOGS_EXPORTER"],
        "otlp",
        StringComparison.OrdinalIgnoreCase);

    // Two projects share a box and both boxes run as "Production", so service.namespace tells the
    // projects apart and deployment.environment tells staging from prod. Without
    // OTEL_DEPLOYMENT_ENVIRONMENT the attribute is left out, never guessed.
    string? deploymentEnvironment = builder.Configuration["OTEL_DEPLOYMENT_ENVIRONMENT"];
    Dictionary<string, object> telemetryResourceAttributes = new()
    {
        ["service.namespace"] = "lotrokoniecdev"
    };
    if (!string.IsNullOrWhiteSpace(deploymentEnvironment))
    {
        telemetryResourceAttributes["deployment.environment"] = deploymentEnvironment;
    }

    builder.Services.AddSerilog((services, lc) =>
    {
        lc.ReadFrom.Configuration(builder.Configuration)
          .ReadFrom.Services(services);

        if (otlpLogExportEnabled)
        {
            lc.WriteTo.OpenTelemetry(opts =>
            {
                opts.Endpoint = otlpEndpoint;
                opts.Protocol = string.Equals(
                    builder.Configuration["OTEL_EXPORTER_OTLP_PROTOCOL"],
                    "http/protobuf",
                    StringComparison.OrdinalIgnoreCase)
                    ? OtlpProtocol.HttpProtobuf
                    : OtlpProtocol.Grpc;
                opts.ResourceAttributes = new Dictionary<string, object>(telemetryResourceAttributes)
                {
                    ["service.name"] = builder.Environment.ApplicationName
                };
            });
        }
    });

    builder.Services.AddAuthSystem(builder.Environment);

    // A kept keyring and a fixed application name, so cookies, antiforgery tokens and e-mailed tokens
    // survive a restart (ADR-0005). Registered before AddAuthentication, which needs the keyring.
    builder.Services.AddAuthDataProtection(builder.Configuration, builder.Environment);

    builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = OpenIddict.Validation.AspNetCore.OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme;
    })
    // AddCookie with our own handler, which gives every sign-in a new session key (ADR-0062). AddCookie
    // cannot name a handler, so the scheme is added directly, and the post-configure line and the
    // Validate call below are the rest of what AddCookie registers.
    .AddScheme<CookieAuthenticationOptions, SignInSessionCookieHandler>(
        Microsoft.AspNetCore.Identity.IdentityConstants.ApplicationScheme,
        options =>
        {
            options.LoginPath = "/Account/Login";
            options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
            options.SlidingExpiration = true;
            options.Cookie.Name = "LotroKoniecDev.Auth";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;

            // A password reset, change or account deletion rotates the stamp, and this drops a cookie
            // that is still valid before it can get fresh tokens from /connect/authorize (#282).
            options.Events.OnValidatePrincipal = SecurityStampCookieValidator.ValidatePrincipalAsync;
        });

    builder.Services.TryAddEnumerable(
        ServiceDescriptor.Singleton<IPostConfigureOptions<CookieAuthenticationOptions>, PostConfigureCookieAuthenticationOptions>());

    // The cookie carries only a session key, and the session lives in the auth database (ADR-0062).
    builder.Services
        .AddOptions<CookieAuthenticationOptions>(Microsoft.AspNetCore.Identity.IdentityConstants.ApplicationScheme)
        .Validate(options => options.Cookie.Expiration == null, "Cookie.Expiration is ignored, use ExpireTimeSpan instead.")
        .Configure<SignInSessionTicketStore>((options, ticketStore) => options.SessionStore = ticketStore);

    // A plain RequireAuthorization means a user's token, not any valid token (#966).
    builder.Services.AddAuthorizationBuilder()
        .SetDefaultPolicy(UserTokenPolicy.Policy);
    builder.Services.AddRazorPages()
        .AddMvcOptions(options => options.Filters.Add(new AntiforgeryFailureFilter()));

    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddOpenApi();

    IOpenTelemetryBuilder openTelemetryBuilder = builder.Services.AddOpenTelemetry()
        .ConfigureResource(resource => resource
            // No service.instance.id: a fresh GUID per process becomes a Prometheus label, which starts
            // a new series on every restart and breaks rate() across a deploy.
            .AddService(builder.Environment.ApplicationName, autoGenerateServiceInstanceId: false)
            .AddAttributes(telemetryResourceAttributes))
        .WithTracing(tracing => tracing
            .AddHttpClientInstrumentation()
            .AddAspNetCoreInstrumentation()
            .AddSource("Npgsql"))
        .WithMetrics(metrics => metrics
            .AddHttpClientInstrumentation()
            .AddAspNetCoreInstrumentation()
            .AddRuntimeInstrumentation()
            .AddMeter("Microsoft.EntityFrameworkCore")
            .AddMeter("Npgsql"));

    // Without an endpoint the SDK would keep retrying its default localhost:4317, which no box runs.
    if (otlpExportEnabled)
    {
        openTelemetryBuilder.UseOtlpExporter();
    }

    builder.Services.AddResponseCompression(options =>
    {
        options.EnableForHttps = true;
        options.Providers.Add<BrotliCompressionProvider>();
        options.Providers.Add<GzipCompressionProvider>();
        options.MimeTypes = ResponseCompressionDefaults.MimeTypes;
    });

    // TLS ends at the proxy, so the X-Forwarded-* headers make Request.Scheme https for OAuth
    // redirects, HATEOAS hrefs and Secure cookies. The token `iss` does not depend on them: it is fixed
    // in OpenIddictSettings.Issuer.
    // Trust is limited to ForwardedHeaders:KnownNetworks (#399), which compose.hetzner.yaml and
    // compose.prod.yaml point at Caddy. An empty list would trust every upstream, which is safe only
    // because both stacks use expose:, not ports:. ForwardLimit = 1 because exactly one proxy sets
    // the headers. A setting that yields no networks, such as a scalar without the __0 index, stops the
    // boot instead of trusting more (ADR-0008 §3).
    IConfigurationSection trustedProxyNetworksSection =
        builder.Configuration.GetSection("ForwardedHeaders:KnownNetworks");
    System.Net.IPNetwork[] trustedProxyNetworks = (trustedProxyNetworksSection.Get<string[]>() ?? [])
        .Select(cidr => System.Net.IPNetwork.Parse(cidr))
        .ToArray();
    if (trustedProxyNetworksSection.Exists() && trustedProxyNetworks.Length == 0)
    {
        throw new InvalidOperationException(
            "ForwardedHeaders:KnownNetworks is set but yielded no networks - "
            + "expected indexed CIDR values (ForwardedHeaders__KnownNetworks__0).");
    }

    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor
                                   | ForwardedHeaders.XForwardedProto
                                   | ForwardedHeaders.XForwardedHost;
        options.ForwardLimit = 1;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        foreach (System.Net.IPNetwork trustedProxyNetwork in trustedProxyNetworks)
        {
            options.KnownIPNetworks.Add(trustedProxyNetwork);
        }
    });

    builder.Services.Configure<RouteHandlerOptions>(options =>
    {
        options.ThrowOnBadRequest = true;
    });

    // The origins come from configuration and CorsSettingsValidator stops the boot in Staging and
    // Production when one is missing or malformed (ADR-0008 §3). Development uses the open policy.
    // AllowCredentials() needs the explicit WithOrigins list: the framework refuses it with AllowAnyOrigin.
    builder.Services.AddOptions<CorsSettings>()
        .BindConfiguration(CorsSettings.ConfigurationSection)
        .ValidateOnStart();
    builder.Services.AddSingleton<IValidateOptions<CorsSettings>, CorsSettingsValidator>();

    string[] allowedCorsOrigins = builder.Configuration
        .GetSection(CorsSettings.ConfigurationSection)
        .Get<CorsSettings>()?.AllowedOrigins ?? [];

    const string localCorsPolicy = "DevelopmentPolicy";
    const string productionCorsPolicy = "ProductionPolicy";

    builder.Services.AddCors(options =>
    {
        options.AddPolicy(localCorsPolicy, corsBuilder =>
        {
            corsBuilder
                .AllowAnyOrigin()
                .AllowAnyHeader()
                .AllowAnyMethod();
        });
        options.AddPolicy(productionCorsPolicy, corsBuilder =>
        {
            corsBuilder
                .WithOrigins(allowedCorsOrigins)
                .WithHeaders("Authorization", "Content-Type", "X-Requested-With", "Accept")
                .WithMethods("GET", "POST", "PUT", "DELETE", "OPTIONS")
                .AllowCredentials();
        });
    });

    builder.Services
        .AddHealthChecks()
        // Not tagged "ready": /health/ready is probed every few seconds, and a database ping there would
        // keep the Neon compute awake all day (ADR-0025). The full /health still runs it.
        .AddNpgSql(
            connectionStringFactory: sp => AuthDatabaseConnectionString.CapOpenTimeout(
                sp.GetRequiredService<IOptions<ConnectionStringSettings>>().Value.AuthDatabase),
            name: "authdb",
            tags: ["db", "postgres"])
        // Not "ready" either: login and token issuance work without mail, so a Brevo outage must not
        // take the service out of rotation.
        .AddCheck<SmtpHealthCheck>(
            "smtp",
            tags: ["smtp"])
        // Not "ready" either: while the broker is down, outbox rows wait and the consumer reconnects.
        // The daily health ping reads the full /health.
        .AddCheck<RabbitMqHealthCheck>(
            "rabbitmq",
            tags: ["broker"]);

    const string rateLimitPolicy = "fixed-by-ip";
    const string authEndpointRateLimitPolicy = "auth-endpoint-limit";
    const string registerRateLimitPolicy = "register-limit";
    const string forgotPasswordRateLimitPolicy = "forgot-password-limit";
    const string resendConfirmationRateLimitPolicy = "resend-confirmation-limit";
    const string changeEmailRateLimitPolicy = "change-email-limit";
    const string authPageRateLimitPolicy = "auth-page-limit";
    const string resendConfirmationPageViewPartition = "resend-confirmation-page-view";
    const string forgotPasswordPageViewPartition = "forgot-password-page-view";
    const string authPageViewPartition = "auth-page-view";

    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

        options.AddPolicy(rateLimitPolicy, httpContext =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: ResolvePartitionKey(httpContext),
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 20,
                    Window = TimeSpan.FromMinutes(1)
                }));

        // Stricter, against brute force. Endpoints that confirm the current password are off it: their
        // brake is the per-account budget behind IPasswordConfirmationThrottle (ADR-0053).
        options.AddPolicy(authEndpointRateLimitPolicy, httpContext =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: ResolvePartitionKey(httpContext),
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window = TimeSpan.FromMinutes(1)
                }));

        // The frontend never calls this endpoint, so it keys on the connection's own address (ADR-0054
        // §3): a leaked frontend key must not buy a fresh mail budget per invented address. The per-inbox
        // half lives behind IRegistrationMailboxThrottle (ADR-0057).
        options.AddPolicy(registerRateLimitPolicy, httpContext =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: ConnectionAddress(httpContext),
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window = TimeSpan.FromMinutes(1)
                }));

        // Very strict, so nobody can flood an inbox. The API endpoint and the Razor page share it, so
        // using both does not double the budget. POST only, like resend-confirmation below. The
        // per-account and per-inbox half lives behind IPasswordResetRequestThrottle (ADR-0057).
        options.AddPolicy(forgotPasswordRateLimitPolicy, httpContext =>
            HttpMethods.IsPost(httpContext.Request.Method)
                ? RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: ConnectionAddress(httpContext),
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 3,
                        Window = TimeSpan.FromMinutes(15)
                    })
                : RateLimitPartition.GetNoLimiter<string>(forgotPasswordPageViewPartition));

        // Mails an address the caller typed, so it is strict like forgot-password. Keyed on the client,
        // not the user: the limiter runs before authentication, so the user is still anonymous here.
        // Behind the frontend the client is the forwarded visitor (ADR-0054, #819). The per-inbox half
        // lives behind IEmailChangeRecipientThrottle (ADR-0057).
        options.AddPolicy(changeEmailRateLimitPolicy, httpContext =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: ResolvePartitionKey(httpContext),
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 3,
                    Window = TimeSpan.FromHours(1)
                }));

        // POST only: a Razor Page is one endpoint for GET and POST, and a budget spent on page views
        // would lock the user out of the form ADR-0046 advertises for a blocked login. The per-inbox half
        // lives behind IEmailConfirmationResendThrottle (ADR-0057).
        options.AddPolicy(resendConfirmationRateLimitPolicy, httpContext =>
            HttpMethods.IsPost(httpContext.Request.Method)
                ? RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: ConnectionAddress(httpContext),
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 3,
                        Window = TimeSpan.FromMinutes(15)
                    })
                : RateLimitPartition.GetNoLimiter<string>(resendConfirmationPageViewPartition));

        // The default for every account page (#692). POST only, like resend-confirmation. The key carries
        // the route as well as the IP, so a few wrong passwords cannot eat the budget for registering.
        // Everyone behind one router shares it, which is accepted: this form is the only place a password
        // can be guessed in production, and the refusal page names the wait.
        options.AddPolicy(authPageRateLimitPolicy, httpContext =>
            HttpMethods.IsPost(httpContext.Request.Method)
                ? RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: BuildPagePostPartitionKey(httpContext),
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        // Room for a few typos and a reset request, far too little to spray with.
                        PermitLimit = 10,
                        Window = TimeSpan.FromMinutes(15)
                    })
                : RateLimitPartition.GetNoLimiter<string>(authPageViewPartition));

        // A fixed window reports its whole length, not the time left, so Retry-After is an upper bound
        // (#892); rounding up keeps it one. A browser gets a Polish page instead of problem-details JSON,
        // because the login and register forms can reach this.
        options.OnRejected = async (context, cancellationToken) =>
        {
            if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter))
            {
                context.HttpContext.Response.Headers.RetryAfter =
                    ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(NumberFormatInfo.InvariantInfo);
            }

            await TooManyRequestsPage.WriteIfBrowserRequestAsync(context.HttpContext, retryAfter, cancellationToken);
        };
    });

    // The route template, never Request.Path: routing ignores casing, so keying on the path would give
    // a fresh budget for every spelling of the same page.
    static string BuildPagePostPartitionKey(HttpContext httpContext)
    {
        string route = httpContext.GetEndpoint() is RouteEndpoint routeEndpoint
            ? routeEndpoint.RoutePattern.RawText ?? string.Empty
            : string.Empty;

        return $"{route}:{ConnectionAddress(httpContext)}";
    }

    // A direct caller's address, or the visitor's address the frontend forwards with its key (ADR-0054
    // §3). Only the policies the frontend reaches use it: fixed-by-ip, auth-endpoint-limit and
    // change-email-limit.
    static string ResolvePartitionKey(HttpContext httpContext) =>
        httpContext.RequestServices.GetRequiredService<RateLimitPartitionKeyResolver>().Resolve(httpContext);

    // For the page policies and register-limit, which the frontend never calls, so honouring the key
    // would only let a leaked key dodge the brake. Keyed like the resolver keys it, so an IPv6 client
    // cannot get a fresh bucket inside its /64 (#831).
    static string ConnectionAddress(HttpContext httpContext) =>
        RateLimitPartitionKeyResolver.KeyFor(httpContext.Connection.RemoteIpAddress);

    WebApplication app = builder.Build();

    // Before anything that reads the scheme or host, so UseHttpsRedirection below sees https and does
    // not loop. Skipped in Development, which runs over plain http with no proxy.
    if (!app.Environment.IsDevelopment())
    {
        app.UseForwardedHeaders();
    }

    app.UseRequestContextLogging();

    app.UseSerilogRequestLogging(options =>
    {
        // The query string is logged separately and redacted, so no OAuth code, token or e-mail is stored.
        options.IncludeQueryInRequestPath = false;
        options.MessageTemplate =
            "HTTP {RequestMethod} {RequestPath}{RequestQuery} responded {StatusCode} in {Elapsed:0.0000} ms";
        options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
        {
            diagnosticContext.Set(
                "RequestQuery",
                SensitiveDataRedactor.RedactQueryString(httpContext.Request.QueryString.Value));

            if (httpContext.Items.TryGetValue("CorrelationId", out object? correlationId))
            {
                diagnosticContext.Set("CorrelationId", correlationId);
            }
        };
    });

    // Off in Development, where the CSP would block Scalar and hot reload; Testing keeps it so the tests
    // check it (#693). It sits outside everything that can end a request early, so a redirect, a CORS
    // preflight or a 429 page carries the headers too.
    if (!app.Environment.IsDevelopment())
    {
        app.UseSecurityHeaders();
    }

    app.UseExceptionHandler();
    app.UseStatusCodePages();
    if (!app.Environment.IsDevelopment() && !app.Environment.IsTesting())
    {
        app.UseHsts();
        app.UseHttpsRedirection();
    }
    app.UseResponseCompression();
    app.UseRouting();
    app.UseGlobalNoCache();

    // Only Development gets the open policy (ADR-0008 §1).
    string corsPolicy = app.Environment.IsDevelopment()
        ? localCorsPolicy
        : productionCorsPolicy;
    app.UseCors(corsPolicy);

    // Before authentication, because OpenIddict ends invalid /connect/* requests there, and a limiter
    // after it would never count that junk traffic. After routing, so it can read the endpoint's policy.
    // Off in Development and Testing; RateLimiting:ForceEnable lets a test host see a real 429.
    bool rateLimiterOffByEnvironment = app.Environment.IsDevelopment() || app.Environment.IsTesting();
    if (!rateLimiterOffByEnvironment || app.Configuration.GetValue<bool>("RateLimiting:ForceEnable"))
    {
        app.UseRateLimiter();
    }

    // Around authentication and authorization, because both can end a call with 401 or 403 without
    // calling the next step (#854).
    app.UseAuthorizationLogging();

    app.UseAuthentication();
    app.UseAuthorization();

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
        app.MapScalarApiReference();
    }

    // The full /health touches the database, mail server and broker, so it needs the health check key
    // (ADR-0058). The two probes below run no checks and stay open.
    app.MapKeyGatedHealthChecks("/health", new HealthCheckOptions
    {
        ResponseWriter = HealthCheckResponseWriter.WriteResponse
    });

    app.MapHealthChecks("/health/live", new HealthCheckOptions
    {
        Predicate = _ => false,
        ResponseWriter = HealthCheckResponseWriter.WriteResponse
    });

    app.MapHealthChecks("/health/ready", new HealthCheckOptions
    {
        Predicate = check => check.Tags.Contains("ready"),
        ResponseWriter = HealthCheckResponseWriter.WriteResponse
    });

    // The metadata is always attached; UseRateLimiter above decides whether it is enforced.
    RouteGroupBuilder endpointsGroup = app.MapGroup("")
        .RequireRateLimiting(rateLimitPolicy);

    app.MapApiEndpoints(endpointsGroup);

    // A group convention reaches only the endpoints mapped through it, so this is what puts the
    // brute-force limiter on OpenIddict's /connect/* endpoints.
    RouteGroupBuilder rootEndpointsGroup = app.MapGroup("")
        .RequireRateLimiting(authEndpointRateLimitPolicy);

    app.MapRootEndpoints(rootEndpointsGroup);

    // Every account page is limited, and a page that wants something else says so with
    // [EnableRateLimiting] or [DisableRateLimiting], so a new page cannot be forgotten (#692).
    app.MapRazorPages().RequireRateLimitingByDefault(authPageRateLimitPolicy);

    // The self-hosted web fonts (LEGAL-06). It sets its own Cache-Control, which GlobalNoCacheMiddleware
    // leaves alone.
    app.MapStaticAssets();

    // Safe to run twice: some integration tests run it again in their arrange step.
    await app.SeedAuthDatabaseAsync();

    await app.RunAsync();
}
#pragma warning disable S2139
catch (Exception ex)
{
    Log.Fatal(ex, "AuthSystem terminated unexpectedly");
    throw;
}
#pragma warning restore S2139
finally
{
    await Log.CloseAndFlushAsync();
}
