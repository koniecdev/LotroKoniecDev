using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Microsoft.Playwright;
using Testcontainers.PostgreSql;

namespace LotroKoniecDev.Frontend.E2E.Tests.Infrastructure;

/// <summary>
/// Starts the whole stack the browser talks to as real containers on one private network: Postgres, the
/// one-shot migrator, RabbitMQ, auth-api, tms-api, the Blazor SSR frontend, Mailpit and a headless
/// Chromium. It then connects Playwright to that browser over a WebSocket.
/// The browser container is built by hand instead of through the <c>Testcontainers.Playwright</c>
/// module; see <see cref="StartBrowserAsync"/> for why that module does not work with current Playwright
/// images.
/// Every service has a DNS alias, so <c>https://auth-api:8443</c> means the same thing to the browser in
/// the container and to the frontend's server-side OIDC calls. That is the single <c>Authority</c>
/// ADR-0006 achieved on the host through <c>localhost</c>, achieved here inside the network through DNS
/// (ADR-0009).
/// HTTPS is required, because the OIDC correlation cookie is <c>SameSite=None</c>. The certificate is
/// generated in C#, and the services trust it through an inline entrypoint that installs the root.
/// Mailpit receives the real confirmation e-mail, so registration is not confirmed automatically and the
/// confirm-link step is real.
/// </summary>
public sealed class PlaywrightStackFixture : IAsyncLifetime
{
    private const string AuthImage = "lotrokoniecdev-auth:fe-e2e";
    private const string TmsImage = "lotrokoniecdev-tms:fe-e2e";
    private const string FrontendImage = "lotrokoniecdev-frontend:fe-e2e";
    private const string MigratorImage = "lotrokoniecdev-migrator:fe-e2e";
    private const string MailpitImage = "axllent/mailpit:latest";

    /// <summary>
    /// Kept at the same version as the compose stacks and the integration suite's
    /// RabbitMqBrokerFixture. The confirmation e-mail travels the outbox, broker and consumer pipeline,
    /// so without a broker in this network Mailpit would never receive the link the register flow waits
    /// for.
    /// </summary>
    private const string RabbitMqImage = "rabbitmq:4.3.4-alpine";

    private const int PlaywrightPort = 8080;

    /// <summary>
    /// The run-server command. It is the same one the <c>Testcontainers.Playwright</c> module produces,
    /// with the driver version read from the image at startup so it matches the client protocol, plus
    /// <c>--host 0.0.0.0</c>. Without that, Playwright 1.55 and later bind the WebSocket server to the
    /// container's loopback address, which the host can never reach through the published port.
    /// </summary>
    private const string PlaywrightServerCommand =
        "npx -y playwright@$(sed --quiet 's/.*\\\"driverVersion\\\": *\"\\([^\"]*\\)\".*/\\1/p' ms-playwright/.docker-info) "
        + "run-server --port 8080 --host 0.0.0.0";

    /// <summary>Image name → Dockerfile path (relative to the solution root), built in order if not present.</summary>
    private static readonly (string Image, string Dockerfile)[] DockerImages =
    [
        (AuthImage, "src/AuthSystem/LotroKoniecDev.AuthSystem.API/Dockerfile"),
        (TmsImage, "src/TranslationSystem/LotroKoniecDev.TranslationSystem.API/Dockerfile"),
        (FrontendImage, "src/Frontend/LotroKoniecDev.Frontend/Dockerfile"),
        (MigratorImage, "Dockerfile.migrator")
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

    // The image's built-in guest account can only connect from the broker's own host, so auth-api inside
    // the network needs a user of its own. The compose stacks do the same.
    private const string RabbitMqUsername = "rabbitmq";
    private const string RabbitMqPassword = "fe-e2e-rabbitmq-password";

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

    /// <summary>Frontend origin, resolved by the in-network browser via the compose DNS alias.</summary>
    public string FrontendBaseUrl => FrontendHttpsOrigin;

    /// <summary>Auth (OpenIddict + Identity Razor Pages) origin, resolved by the in-network browser.</summary>
    public string AuthBaseUrl => AuthHttpsOrigin;

    /// <summary>Mailpit HTTP API, reached from the host test process over the mapped port.</summary>
    public string MailpitBaseUrl => $"http://localhost:{_mailpit.GetMappedPublicPort(MailpitHttpPort)}";

    private static string TranslationConnectionString => BuildConnectionString(TranslationDatabaseName);
    private static string AuthConnectionString => BuildConnectionString(AuthDatabaseName);

    public async Task InitializeAsync()
    {
        GenerateCertificate();
        await BuildDockerImagesAsync();

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
        // POSTGRES_DB creates lotro_translation on the first boot, and the mounted init script adds the
        // second database, lotro_auth, that the AuthSystem needs. The compose stack does the same.
        string initScriptPath = Path.Combine(FindSolutionDirectory(), "scripts", "init-postgres.sh");
        _postgres = new PostgreSqlBuilder("postgres:17-alpine")
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
        _rabbitMq = new ContainerBuilder(RabbitMqImage)
            .WithNetwork(_network)
            .WithNetworkAliases("rabbitmq")
            .WithEnvironment("RABBITMQ_DEFAULT_USER", RabbitMqUsername)
            .WithEnvironment("RABBITMQ_DEFAULT_PASS", RabbitMqPassword)
            // Log-based readiness rather than an exec probe: exec'ing into a container that is
            // still starting throws out of the wait strategy under Docker load, failing the whole
            // fixture. The auth-api consumer retries with backoff anyway, so "started" is enough.
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilMessageIsLogged("Server startup complete"))
            .Build();
        await _rabbitMq.StartAsync();
    }

    private async Task RunMigratorAsync()
    {
        _migrator = new ContainerBuilder(MigratorImage)
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
        // Testing profile: seeds a deterministic admin + the OpenIddict clients. The web client's
        // redirect/post-logout URIs and the issuer are pointed at the in-network HTTPS origins, and
        // Email targets Mailpit so a registration actually sends a confirmation link (no auto-confirm).
        _authApi = new ContainerBuilder(AuthImage)
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
            // The confirmation e-mail travels outbox -> broker -> consumer -> Mailpit, so the
            // register flow's link genuinely rides the whole pipeline this suite exists to prove.
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
        // tms validates JWTs against the issuer over HTTPS (JWKS from https://auth-api:8443), so it
        // trusts the e2e cert via the inline entrypoint. Not strictly exercised by the auth loop, but
        // kept wired so the stack is complete and the later editor/list flows have a target.
        _tmsApi = new ContainerBuilder(TmsImage)
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
        // The RP's single Authority + the typed tms client both back-channel over HTTPS, so the FE
        // trusts the e2e cert via the inline entrypoint. The browser hits the FE at the same origin.
        _frontend = new ContainerBuilder(FrontendImage)
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
            // The frontend has no health endpoint, and UseHttpsRedirection turns every HTTP request into
            // a 307 to the HTTPS origin, which the waiting HttpClient follows to a port that is not
            // published, and fails.
            // So we wait for the startup log line instead: redirects cannot affect it and it is always
            // the same. The browser still reaches the frontend over HTTPS inside the network on :8443.
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Application started"))
            .Build();

        await StartWithDiagnosticsAsync(_frontend, "frontend");
    }

    private async Task StartBrowserAsync()
    {
        // Built by hand instead of via PlaywrightBuilder: that module omits run-server's --host flag
        // and hard-codes a "Listening on ws://localhost:8080/" readiness probe. Since Playwright v1.55+
        // run-server binds to the container loopback (logging "ws://[::1]:8080/") unless --host is given,
        // the module's probe never matches (so StartAsync hangs in the wait strategy) AND the loopback-
        // bound server is unreachable through the published port (so ConnectAsync would hang too). Its
        // wait strategies are append-only, so the broken probe can't be replaced. Binding 0.0.0.0 fixes
        // both: the WebSocket server is reachable via the mapped port and logs the address we wait on.
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
    /// Installs the test CA into the OS trust store as root and then starts the app. .NET checks the
    /// certificate against the OS store and ignores SSL_CERT_FILE, so the CA has to be there before
    /// Kestrel and HttpClient start. It is written inline, so no entrypoint script has to be committed.
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
        // Limit how long we wait, so a container that is really unhealthy fails within minutes. The
        // default strategy would keep retrying for about an hour.
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(5));
        try
        {
            await container.StartAsync(cts.Token);
        }
        catch (Exception ex)
        {
            // Never let log retrieval mask the real failure: a container that died before it was
            // created has no Id, so GetLogsAsync would itself throw and swallow ex.
            throw new InvalidOperationException(
                $"Container '{name}' failed to reach its ready state within the timeout.\n" +
                $"{await TryGetLogsAsync(container)}", ex);
        }
    }

    /// <summary>
    /// The auth-api container's logs. They are the only way to see what the outbox relay and the broker
    /// consumer did when an e-mail does not reach Mailpit. The TMS E2E fixture has the same helper.
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

    private static async Task BuildDockerImagesAsync()
    {
        // CI pre-builds the images and sets SKIP_DOCKER_BUILD=true; locally the suite builds them on demand.
        if (Environment.GetEnvironmentVariable("SKIP_DOCKER_BUILD") == "true")
        {
            Console.WriteLine("Skipping Docker image build (SKIP_DOCKER_BUILD=true).");
            return;
        }

        string solutionDir = FindSolutionDirectory();
        string cacheBust = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

        foreach ((string imageName, string dockerfile) in DockerImages)
        {
            Console.WriteLine($"Building Docker image: {imageName}...");

            using Process process = new();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = $"build --build-arg CACHEBUST={cacheBust} -f {dockerfile} -t {imageName} .",
                WorkingDirectory = solutionDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            process.Start();

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                string stdout = await stdoutTask;
                string stderr = await stderrTask;
                throw new InvalidOperationException(
                    $"Failed to build Docker image '{imageName}' (exit code {process.ExitCode}).\n" +
                    $"Dockerfile: {dockerfile}\nWorking directory: {solutionDir}\n" +
                    $"Stdout:\n{stdout}\nStderr:\n{stderr}");
            }

            Console.WriteLine($"Successfully built: {imageName}");
        }
    }

    private static string FindSolutionDirectory()
    {
        DirectoryInfo? directory = new(Directory.GetCurrentDirectory());

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LotroKoniecDev.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not find the solution directory (LotroKoniecDev.slnx). Started from: {Directory.GetCurrentDirectory()}");
    }

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
