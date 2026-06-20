using System.Diagnostics;
using System.Globalization;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Testcontainers.PostgreSql;

namespace LotroKoniecDev.TranslationSystem.E2E.Tests;

/// <summary>
/// Boots the full TMS backend the way it ships — Postgres + the one-shot migrator + auth-api + tms-api —
/// as real containers on a private Docker network, so the suite exercises the core loop over actual HTTP
/// through real JWT issuance, JWKS validation and lazy translator provisioning. This is the layer the
/// in-process integration tests cannot reach (they forge HS256 tokens and disable JWKS discovery).
/// </summary>
public sealed class E2ETestFixture : IAsyncLifetime
{
    private const string MigratorImage = "lotrokoniecdev-migrator:e2e";
    private const string AuthImage = "lotrokoniecdev-auth:e2e";
    private const string TmsImage = "lotrokoniecdev-tms:e2e";

    /// <summary>Image name → Dockerfile path (relative to the solution root), built in order if not present.</summary>
    private static readonly (string Image, string Dockerfile)[] DockerImages =
    [
        (AuthImage, "src/AuthSystem/LotroKoniecDev.AuthSystem.API/Dockerfile"),
        (TmsImage, "src/TranslationSystem/LotroKoniecDev.TranslationSystem.API/Dockerfile"),
        (MigratorImage, "Dockerfile.migrator")
    ];

    private const string TranslationDatabaseName = "lotro_translation";
    private const string AuthDatabaseName = "lotro_auth";
    private const string PostgresUser = "postgres";
    private const string PostgresPassword = "e2e-postgres-password";

    /// <summary>The audience tms-api validates; matches the resource the auth token endpoint stamps on the JWT.</summary>
    private const string Audience = "lotrokoniecdev-api";
    private const string ApiClientSecret = "e2e-test-api-client-secret-min-32-characters";

    /// <summary>
    /// Seeded by auth-api on startup from <c>AdminUser__*</c> config, with <c>EmailConfirmed = true</c> and the
    /// <c>Admin</c> role — so a password-grant login yields a real Admin token without the email-confirmation dance.
    /// </summary>
    public const string AdminUsername = "e2e-admin";
    public const string AdminEmail = "e2e-admin@lotro.koniec.dev";
    public const string AdminPassword = "E2eAdminPass123!";

    /// <summary>The public, password-grant OpenIddict client seeded only under the <c>Testing</c> environment.</summary>
    public const string TestClientId = "lotrokoniecdev-test";

    private INetwork _network = null!;
    private PostgreSqlContainer _postgres = null!;
    private IContainer _migrator = null!;
    private IContainer _authApi = null!;
    private IContainer _tmsApi = null!;

    public string AuthApiBaseUrl => $"http://localhost:{_authApi.GetMappedPublicPort(8080)}";
    public string TmsApiBaseUrl => $"http://localhost:{_tmsApi.GetMappedPublicPort(8080)}";

    private static string TranslationConnectionString => BuildInternalConnectionString(TranslationDatabaseName);
    private static string AuthConnectionString => BuildInternalConnectionString(AuthDatabaseName);

    public async Task InitializeAsync()
    {
        await BuildDockerImagesAsync();

        _network = new NetworkBuilder()
            .WithName($"e2e-{Guid.NewGuid():N}")
            .Build();
        await _network.CreateAsync();

        // Postgres' POSTGRES_DB creates lotro_translation on first boot; the bind-mounted init script
        // adds the second database (lotro_auth) the AuthSystem needs — identical to the compose stack.
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

        await RunMigratorAsync();
        await StartApiContainersAsync();
    }

    /// <summary>
    /// Truncates the translation lifecycle tables so each loop test starts from an empty catalog and can assert
    /// exact counts. Runs <c>psql</c> inside the Postgres container (local-socket trust), mirroring the in-process
    /// <c>CoreLoopTests</c> reset; the <c>Translators</c> table is intentionally left (the provisioner is idempotent).
    /// </summary>
    public async Task ResetTranslationDataAsync()
    {
        ExecResult result = await _postgres.ExecAsync(
        [
            "psql", "-v", "ON_ERROR_STOP=1", "-U", PostgresUser, "-d", TranslationDatabaseName,
            "-c", "TRUNCATE translation.\"Translations\", translation.\"GameVersions\", translation.\"TranslationArtifacts\" CASCADE;"
        ]);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Failed to reset translation data (exit code {result.ExitCode}).\nStdout:\n{result.Stdout}\nStderr:\n{result.Stderr}");
        }
    }

    public async Task<string> GetAuthApiLogsAsync()
    {
        (string? stdout, string? stderr) = await _authApi.GetLogsAsync();
        return $"=== Auth API Stdout ===\n{stdout}\n=== Auth API Stderr ===\n{stderr}";
    }

    public async Task<string> GetTmsApiLogsAsync()
    {
        (string? stdout, string? stderr) = await _tmsApi.GetLogsAsync();
        return $"=== TMS API Stdout ===\n{stdout}\n=== TMS API Stderr ===\n{stderr}";
    }

    private async Task RunMigratorAsync()
    {
        // One-shot: migrates the TMS context (via Persistence) then the Auth context (via the Auth API),
        // exactly as the compose migrator does. Mirrors compose env — connection strings only.
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

    private async Task StartApiContainersAsync()
    {
        // No RegisterUser->CreatePerson saga is lifted (the translator profile is provisioned lazily on the first
        // authenticated TMS request), so there is no auth->tms startup dependency. auth-api is started first only
        // because tms-api fetches its JWKS from it; the seeded admin must also be live before any test logs in.
        _authApi = new ContainerBuilder(AuthImage)
            .WithNetwork(_network)
            .WithNetworkAliases("auth-api")
            .WithPortBinding(8080, true)
            .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Testing")
            .WithEnvironment("ASPNETCORE_URLS", "http://+:8080")
            .WithEnvironment("ConnectionStrings__AuthDatabase", AuthConnectionString)
            // The token iss; tms-api validates against this and fetches its JWKS from the same in-network address.
            .WithEnvironment("OpenIddict__Issuer", "http://auth-api:8080")
            .WithEnvironment("OpenIddict__ApiClientSecret", ApiClientSecret)
            // The seeder dereferences WebClient.RedirectUris[0] in non-production, so at least one URI is required.
            .WithEnvironment("OpenIddict__WebClient__RedirectUris__0", "http://localhost:8080/callback")
            .WithEnvironment("OpenIddict__WebClient__PostLogoutRedirectUris__0", "http://localhost:8080")
            .WithEnvironment("AdminUser__Username", AdminUsername)
            .WithEnvironment("AdminUser__Email", AdminEmail)
            .WithEnvironment("AdminUser__Password", AdminPassword)
            // No mailpit in this network: the confirmation-email send fails fast and registration auto-confirms,
            // so a freshly-registered translator can log in without the email round-trip. The sender identity is
            // no longer baked into base appsettings.json (M6-06), so it is injected here too — otherwise the
            // unconditional EmailOptionsValidator would abort auth-api startup.
            .WithEnvironment("Email__SenderEmail", "noreply@lotro.koniec.dev")
            .WithEnvironment("Email__Sender", "lotro.koniec.dev")
            .WithEnvironment("Email__Host", "localhost")
            .WithEnvironment("Email__Port", "2525")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(request => request.ForPath("/health/live").ForPort(8080)))
            .Build();

        await _authApi.StartAsync();

        _tmsApi = new ContainerBuilder(TmsImage)
            .WithNetwork(_network)
            .WithNetworkAliases("tms-api")
            .WithPortBinding(8080, true)
            .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
            .WithEnvironment("ASPNETCORE_URLS", "http://+:8080")
            .WithEnvironment("ConnectionStrings__TranslationDatabase", TranslationConnectionString)
            .WithEnvironment("Auth__Issuer", "http://auth-api:8080")
            .WithEnvironment("Auth__Authority", "http://auth-api:8080")
            .WithEnvironment("Auth__Audience", Audience)
            .WithEnvironment("Bootstrap__Enabled", "false")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(request => request.ForPath("/health/live").ForPort(8080)))
            .Build();

        await _tmsApi.StartAsync();
    }

    private static string BuildInternalConnectionString(string database) =>
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

            string stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                string stdout = await stdoutTask;
                throw new InvalidOperationException(
                    $"Failed to build Docker image '{imageName}' (exit code {process.ExitCode}).\n" +
                    $"Dockerfile: {dockerfile}\n" +
                    $"Working directory: {solutionDir}\n" +
                    $"Stdout:\n{stdout}\n" +
                    $"Stderr:\n{stderr}");
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
        if (_tmsApi is not null)
        {
            await _tmsApi.DisposeAsync();
        }

        if (_authApi is not null)
        {
            await _authApi.DisposeAsync();
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
