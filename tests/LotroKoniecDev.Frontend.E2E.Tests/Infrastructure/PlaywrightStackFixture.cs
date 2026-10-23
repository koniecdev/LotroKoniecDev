using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using LotroKoniecDev.Tests.Shared;
using Microsoft.Playwright;
using Testcontainers.PostgreSql;

namespace LotroKoniecDev.Frontend.E2E.Tests.Infrastructure;

/// <summary>
/// The whole browser-facing stack as real containers on one network, with Playwright connected to an
/// in-network Chromium (ADR-0009). DNS aliases make <c>https://auth-api:8443</c> mean the same to the
/// browser and to the frontend's OIDC back-channel: the single <c>Authority</c> of ADR-0006. HTTPS is
/// required because the OIDC correlation cookie is <c>SameSite=None</c>. Mailpit receives the real
/// confirmation e-mail, so the confirm-link step is real.
/// </summary>
public sealed class PlaywrightStackFixture : IAsyncLifetime
{
    private const string AuthRepository = "lotrokoniecdev-auth";
    private const string TmsRepository = "lotrokoniecdev-tms";
    private const string FrontendRepository = "lotrokoniecdev-frontend";
    private const string MigratorRepository = "lotrokoniecdev-migrator";
    private const string MailpitImage = "axllent/mailpit:latest";

    private const int PlaywrightPort = 8080;

    /// <summary>
    /// The <c>Testcontainers.Playwright</c> module's command plus <c>--host 0.0.0.0</c>: without it,
    /// Playwright 1.55+ binds to the container's loopback, which the published port cannot reach.
    /// </summary>
    private const string PlaywrightServerCommand =
        "npx -y playwright@$(sed --quiet 's/.*\\\"driverVersion\\\": *\"\\([^\"]*\\)\".*/\\1/p' ms-playwright/.docker-info) "
        + "run-server --port 8080 --host 0.0.0.0";

    /// <summary>Dockerfile paths are relative to the solution root; built in this order.</summary>
    private static readonly (string Repository, string Dockerfile)[] DockerImages =
    [
        (AuthRepository, "src/AuthSystem/LotroKoniecDev.AuthSystem.API/Dockerfile"),
        (TmsRepository, "src/TranslationSystem/LotroKoniecDev.TranslationSystem.API/Dockerfile"),
        (FrontendRepository, "src/Frontend/LotroKoniecDev.Frontend/Dockerfile"),
        (MigratorRepository, "Dockerfile.migrator")
    ];

    private const int HttpsPort = 8443;
    private const int HttpPort = 8080;
    private const int MailpitHttpPort = 8025;
    private const int MailpitSmtpPort = 1025;
    private const string KestrelUrls = "https://+:8443;http://+:8080";

    private const string AuthHttpsOrigin = "https://auth-api:8443";
    private const string TmsHttpsOrigin = "https://tms-api:8443";
    private const string FrontendHttpsOrigin = "https://frontend:8443";

    private const string TranslationDatabaseName = "lotro_translation";
    private const string AuthDatabaseName = "lotro_auth";
    private const string PostgresUser = "postgres";
    private const string PostgresPassword = "fe-e2e-postgres-password";

    private const string Audience = "lotrokoniecdev-api";
    private const string ApiClientSecret = "fe-e2e-api-client-secret-min-32-characters";
    private const string WebClientId = "lotrokoniecdev-web";

    private const string AdminUsername = "fee2eadmin";
    private const string AdminEmail = "fe-e2e-admin@lotro-translator.pl";
    private const string AdminPassword = "FeE2eAdminPass123!";

    // The image's guest account can connect only from the broker's own host, so auth-api needs a user of
    // its own, as in the compose stacks.
    private const string RabbitMqUsername = "rabbitmq";
    private const string RabbitMqPassword = "fe-e2e-rabbitmq-password";

    private E2EWorktree _worktree = null!;

    private string _certPem = null!;
    private string _keyPem = null!;

    private INetwork _network = null!;
    private PostgreSqlContainer _postgres = null!;
    private IContainer _migrator = null!;
    private IContainer _mailpit = null!;
    private IContainer _rabbitMq = null!;
    private IContainer _authApi = null!;
    private IContainer _tmsApi = null!;
    private IContainer _frontend = null!;
    private IContainer _playwrightContainer = null!;
    private IPlaywright _playwright = null!;

    public IBrowser Browser { get; private set; } = null!;

    public string FrontendBaseUrl => FrontendHttpsOrigin;

    public string AuthBaseUrl => AuthHttpsOrigin;

    /// <summary>Reached from the host test process over the mapped port.</summary>
    public string MailpitBaseUrl => $"http://localhost:{_mailpit.GetMappedPublicPort(MailpitHttpPort)}";

    private static string TranslationConnectionString => BuildConnectionString(TranslationDatabaseName);
    private static string AuthConnectionString => BuildConnectionString(AuthDatabaseName);

    public async Task InitializeAsync()
    {
        GenerateCertificate();
        _worktree = new E2EWorktree("fe-e2e");
        await _worktree.BuildImagesAsync(DockerImages);

        _network = new NetworkBuilder()
            .WithName($"fe-e2e-{Guid.NewGuid():N}")
            .Build();
        await _network.CreateAsync();

        await StartPostgresAsync();
        await StartMailpitAsync();
        await StartRabbitMqAsync();
        await RunMigratorAsync();
        await StartAuthApiAsync();
        await StartTmsApiAsync();
        await StartFrontendAsync();
        await StartBrowserAsync();
    }

    private async Task StartPostgresAsync()
    {
        // The mounted init script adds the second database, lotro_auth, as the compose stack does.
        string initScriptPath = Path.Combine(_worktree.SolutionDirectory, "scripts", "init-postgres.sh");
        _postgres = new PostgreSqlBuilder(PostgresImage.Name)
            .WithNetwork(_network)
            .WithNetworkAliases("postgres")
            .WithUsername(PostgresUser)
            .WithPassword(PostgresPassword)
            .WithDatabase(TranslationDatabaseName)
            .WithBindMount(initScriptPath, "/docker-entrypoint-initdb.d/10-init-databases.sh")
            .Build();
        await _postgres.StartAsync();
    }

    private async Task StartMailpitAsync()
    {
        _mailpit = new ContainerBuilder(MailpitImage)
            .WithNetwork(_network)
            .WithNetworkAliases("mailpit")
            .WithPortBinding(MailpitHttpPort, true)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(request => request.ForPath("/").ForPort(MailpitHttpPort)))
            .Build();
        await _mailpit.StartAsync();
    }

    private async Task StartRabbitMqAsync()
    {
        // The confirmation e-mail travels through the broker, so Mailpit needs it.
        _rabbitMq = new ContainerBuilder(RabbitMqImage.Name)
            .WithNetwork(_network)
            .WithNetworkAliases("rabbitmq")
            .WithEnvironment("RABBITMQ_DEFAULT_USER", RabbitMqUsername)
            .WithEnvironment("RABBITMQ_DEFAULT_PASS", RabbitMqPassword)
            // Not an exec probe: exec into a starting container throws under Docker load. The consumer
            // retries anyway, so "started" is enough.
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilMessageIsLogged("Server startup complete"))
            .Build();
        await _rabbitMq.StartAsync();
    }

    private async Task RunMigratorAsync()
    {
        _migrator = new ContainerBuilder(_worktree.Image(MigratorRepository))
            .WithNetwork(_network)
            .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
            .WithEnvironment("ConnectionStrings__TranslationDatabase", TranslationConnectionString)
            .WithEnvironment("ConnectionStrings__AuthDatabase", AuthConnectionString)
            .Build();

        await _migrator.StartAsync();

        long exitCode = await _migrator.GetExitCodeAsync();
        if (exitCode != 0)
        {
            (string? stdout, string? stderr) = await _migrator.GetLogsAsync();
            throw new InvalidOperationException(
                $"Migrator failed with exit code {exitCode}.\nStdout:\n{stdout}\nStderr:\n{stderr}");
        }
    }

    private async Task StartAuthApiAsync()
    {
        // Testing profile: seeds an admin and the OpenIddict clients, pointed at the in-network origins.
        _authApi = new ContainerBuilder(_worktree.Image(AuthRepository))
            .WithNetwork(_network)
            .WithNetworkAliases("auth-api")
            .WithPortBinding(HttpPort, true)
            .WithCreateParameterModifier(parameters => parameters.User = "0")
            .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Testing")
            .WithEnvironment("ASPNETCORE_URLS", KestrelUrls)
            .WithEnvironment("ConnectionStrings__AuthDatabase", AuthConnectionString)
            .WithEnvironment("OpenIddict__Issuer", AuthHttpsOrigin)
            .WithEnvironment("OpenIddict__ApiClientSecret", ApiClientSecret)
            .WithEnvironment("OpenIddict__WebClient__RedirectUris__0", $"{FrontendHttpsOrigin}/callback")
            .WithEnvironment("OpenIddict__WebClient__PostLogoutRedirectUris__0", FrontendHttpsOrigin)
            .WithEnvironment("OpenIddict__WebClient__PostLogoutRedirectUris__1", $"{FrontendHttpsOrigin}/")
            .WithEnvironment("OpenIddict__WebClient__PostLogoutRedirectUris__2", $"{FrontendHttpsOrigin}/signout-callback-oidc")
            .WithEnvironment("AdminUser__Username", AdminUsername)
            .WithEnvironment("AdminUser__Email", AdminEmail)
            .WithEnvironment("AdminUser__Password", AdminPassword)
            .WithEnvironment("Email__SenderEmail", "noreply@lotro-translator.pl")
            .WithEnvironment("Email__Sender", "lotro-translator.pl")
            .WithEnvironment("Email__Host", "mailpit")
            .WithEnvironment("Email__Port", MailpitSmtpPort.ToString(CultureInfo.InvariantCulture))
            .WithEnvironment("Email__Mode", "None")
            .WithEnvironment("RabbitMq__Host", "rabbitmq")
            .WithEnvironment("RabbitMq__Username", RabbitMqUsername)
            .WithEnvironment("RabbitMq__Password", RabbitMqPassword)
            .WithEnvironment("ASPNETCORE_Kestrel__Certificates__Default__Path", "/certs/e2e.crt")
            .WithEnvironment("ASPNETCORE_Kestrel__Certificates__Default__KeyPath", "/certs/e2e.key")
            .WithResourceMapping(Encoding.ASCII.GetBytes(_certPem), "/certs/e2e.crt")
            .WithResourceMapping(Encoding.ASCII.GetBytes(_keyPem), "/certs/e2e.key")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(request => request.ForPath("/health/live").ForPort(HttpPort)))
            .Build();

        await StartWithDiagnosticsAsync(_authApi, "auth-api");
    }

    private async Task StartTmsApiAsync()
    {
        // tms reads the issuer's keys over HTTPS, so it trusts the e2e cert too.
        _tmsApi = new ContainerBuilder(_worktree.Image(TmsRepository))
            .WithNetwork(_network)
            .WithNetworkAliases("tms-api")
            .WithPortBinding(HttpPort, true)
            .WithCreateParameterModifier(parameters => parameters.User = "0")
            .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
            .WithEnvironment("ASPNETCORE_URLS", KestrelUrls)
            .WithEnvironment("ConnectionStrings__TranslationDatabase", TranslationConnectionString)
            .WithEnvironment("Auth__Issuer", AuthHttpsOrigin)
            .WithEnvironment("Auth__Authority", AuthHttpsOrigin)
            .WithEnvironment("Auth__Audience", Audience)
            // The account event consumer of ADR-0065 reads the same broker as auth-api.
            .WithEnvironment("RabbitMq__Host", "rabbitmq")
            .WithEnvironment("RabbitMq__Username", RabbitMqUsername)
            .WithEnvironment("RabbitMq__Password", RabbitMqPassword)
            .WithEnvironment("ASPNETCORE_Kestrel__Certificates__Default__Path", "/certs/e2e.crt")
            .WithEnvironment("ASPNETCORE_Kestrel__Certificates__Default__KeyPath", "/certs/e2e.key")
            .WithResourceMapping(Encoding.ASCII.GetBytes(_certPem), "/certs/e2e.crt")
            .WithResourceMapping(Encoding.ASCII.GetBytes(_keyPem), "/certs/e2e.key")
            .WithEntrypoint("/bin/sh", "-c", TrustThenRun("LotroKoniecDev.TranslationSystem.API.dll"))
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(request => request.ForPath("/health/live").ForPort(HttpPort)))
            .Build();

        await StartWithDiagnosticsAsync(_tmsApi, "tms-api");
    }

    private async Task StartFrontendAsync()
    {
        // The frontend's back-channels run over HTTPS, so it trusts the e2e cert too.
        _frontend = new ContainerBuilder(_worktree.Image(FrontendRepository))
            .WithNetwork(_network)
            .WithNetworkAliases("frontend")
            .WithPortBinding(HttpPort, true)
            .WithCreateParameterModifier(parameters => parameters.User = "0")
            .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
            .WithEnvironment("ASPNETCORE_URLS", KestrelUrls)
            .WithEnvironment("AuthSystem__Authority", AuthHttpsOrigin)
            .WithEnvironment("AuthSystem__BaseUrl", $"{AuthHttpsOrigin}/")
            .WithEnvironment("AuthSystem__ClientId", WebClientId)
            .WithEnvironment("TranslationSystem__BaseUrl", $"{TmsHttpsOrigin}/")
            .WithEnvironment("ASPNETCORE_Kestrel__Certificates__Default__Path", "/certs/e2e.crt")
            .WithEnvironment("ASPNETCORE_Kestrel__Certificates__Default__KeyPath", "/certs/e2e.key")
            .WithResourceMapping(Encoding.ASCII.GetBytes(_certPem), "/certs/e2e.crt")
            .WithResourceMapping(Encoding.ASCII.GetBytes(_keyPem), "/certs/e2e.key")
            .WithEntrypoint("/bin/sh", "-c", TrustThenRun("LotroKoniecDev.Frontend.dll"))
            // An HTTP probe gets a 307 to a port that is not published, so wait for the log line instead.
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Application started"))
            .Build();

        await StartWithDiagnosticsAsync(_frontend, "frontend");
    }

    private async Task StartBrowserAsync()
    {
        // Not PlaywrightBuilder: it omits --host and hard-codes a "ws://localhost:8080/" readiness probe,
        // which never matches on Playwright 1.55+, and its wait strategies cannot be replaced.
        _playwrightContainer = new ContainerBuilder(PlaywrightBrowserImage.Tag)
            .WithNetwork(_network)
            .WithEntrypoint("/bin/sh", "-c")
            .WithCommand(PlaywrightServerCommand)
            .WithPortBinding(PlaywrightPort, true)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilMessageIsLogged($"Listening on ws://0.0.0.0:{PlaywrightPort}"))
            .Build();

        await StartWithDiagnosticsAsync(_playwrightContainer, "playwright");

        _playwright = await Playwright.CreateAsync();
        string browserWsEndpoint = new UriBuilder(
            "ws", _playwrightContainer.Hostname, _playwrightContainer.GetMappedPublicPort(PlaywrightPort)).ToString();
        Browser = await _playwright.Chromium.ConnectAsync(
            browserWsEndpoint, new BrowserTypeConnectOptions { Timeout = 60_000 });
    }

    /// <summary>
    /// .NET ignores SSL_CERT_FILE and checks the OS store, so the CA must be installed there before the app
    /// starts.
    /// </summary>
    private static string TrustThenRun(string dll) =>
        "cp /certs/e2e.crt /usr/local/share/ca-certificates/lotro-e2e.crt && " +
        "update-ca-certificates >/dev/null 2>&1; " +
        $"exec dotnet {dll}";

    private void GenerateCertificate()
    {
        using RSA rsa = RSA.Create(2048);
        CertificateRequest request = new(
            "CN=lotro-e2e", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        SubjectAlternativeNameBuilder san = new();
        san.AddDnsName("localhost");
        san.AddDnsName("auth-api");
        san.AddDnsName("tms-api");
        san.AddDnsName("frontend");
        san.AddDnsName("mailpit");
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.KeyCertSign,
                critical: true));

        using X509Certificate2 certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(825));

        _certPem = certificate.ExportCertificatePem();
        _keyPem = rsa.ExportPkcs8PrivateKeyPem();
    }

    private static async Task StartWithDiagnosticsAsync(IContainer container, string name)
    {
        // The default strategy would keep retrying for about an hour.
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(5));
        try
        {
            await container.StartAsync(cts.Token);
        }
        catch (Exception ex)
        {
            // A container that died before it was created has no Id, and GetLogsAsync would throw.
            throw new InvalidOperationException(
                $"Container '{name}' failed to reach its ready state within the timeout.\n" +
                $"{await TryGetLogsAsync(container)}", ex);
        }
    }

    /// <summary>
    /// The only way to see what the outbox relay and the consumer did when an e-mail does not reach Mailpit.
    /// </summary>
    public async Task<string> GetAuthApiLogsAsync()
    {
        return await TryGetLogsAsync(_authApi);
    }

    private static async Task<string> TryGetLogsAsync(IContainer container)
    {
        try
        {
            (string stdout, string stderr) = await container.GetLogsAsync();
            return $"Stdout:\n{stdout}\nStderr:\n{stderr}";
        }
        catch (Exception ex)
        {
            return $"(container logs unavailable: {ex.Message})";
        }
    }

    private static string BuildConnectionString(string database) =>
        $"Host=postgres;Port=5432;Database={database};Username={PostgresUser};Password={PostgresPassword}";

    public async Task DisposeAsync()
    {
        if (Browser is not null)
        {
            await Browser.DisposeAsync();
        }

        _playwright?.Dispose();

        if (_playwrightContainer is not null)
        {
            await _playwrightContainer.DisposeAsync();
        }

        if (_frontend is not null)
        {
            await _frontend.DisposeAsync();
        }

        if (_tmsApi is not null)
        {
            await _tmsApi.DisposeAsync();
        }

        if (_authApi is not null)
        {
            await _authApi.DisposeAsync();
        }

        if (_rabbitMq is not null)
        {
            await _rabbitMq.DisposeAsync();
        }

        if (_mailpit is not null)
        {
            await _mailpit.DisposeAsync();
        }

        if (_migrator is not null)
        {
            await _migrator.DisposeAsync();
        }

        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }

        if (_network is not null)
        {
            await _network.DeleteAsync();
        }
    }
}
