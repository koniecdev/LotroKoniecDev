using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using LotroKoniecDev.Tests.Shared;
using Testcontainers.PostgreSql;

namespace LotroKoniecDev.TranslationSystem.E2E.Tests;

/// <summary>
/// The TMS backend as it ships, as real containers on a private network, so the core loop runs through
/// real token issuance, JWKS validation and provisioning: the layer the in-process tests fake with
/// HS256 tokens.
/// </summary>
public sealed class E2ETestFixture : IAsyncLifetime
{
    private const string MigratorRepository = "lotrokoniecdev-migrator";
    private const string AuthRepository = "lotrokoniecdev-auth";
    private const string TmsRepository = "lotrokoniecdev-tms";

    /// <summary>Dockerfile paths relative to the solution root, built in this order.</summary>
    private static readonly (string Repository, string Dockerfile)[] DockerImages =
    [
        (AuthRepository, "src/AuthSystem/LotroKoniecDev.AuthSystem.API/Dockerfile"),
        (TmsRepository, "src/TranslationSystem/LotroKoniecDev.TranslationSystem.API/Dockerfile"),
        (MigratorRepository, "Dockerfile.migrator")
    ];

    private const string TranslationDatabaseName = "lotro_translation";
    private const string AuthDatabaseName = "lotro_auth";
    private const string PostgresUser = "postgres";
    private const string PostgresPassword = "e2e-postgres-password";

    /// <summary>Must match the resource the auth token endpoint stamps on the token.</summary>
    private const string Audience = "lotrokoniecdev-api";
    private const string ApiClientSecret = "e2e-test-api-client-secret-min-32-characters";

    /// <summary>
    /// Seeded by auth-api from <c>AdminUser__*</c> with a confirmed e-mail, so a password login gives a real
    /// admin token.
    /// </summary>
    public const string AdminUsername = "e2eadmin";
    public const string AdminEmail = "e2e-admin@lotro-translator.pl";
    public const string AdminPassword = "E2eAdminPass123!";

    /// <summary>Seeded only in the <c>Testing</c> environment.</summary>
    public const string TestClientId = "lotrokoniecdev-test";

    private E2EWorktree _worktree = null!;

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
        _worktree = new E2EWorktree("e2e");
        await _worktree.BuildImagesAsync(DockerImages);

        _network = new NetworkBuilder()
            .WithName($"e2e-{Guid.NewGuid():N}")
            .Build();
        await _network.CreateAsync();

        // As in compose: POSTGRES_DB creates lotro_translation, the init script adds lotro_auth.
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

        await RunMigratorAsync();
        await StartApiContainersAsync();
    }

    /// <summary>
    /// Runs <c>psql</c> inside the container (local-socket trust). <c>Translators</c> stays, because
    /// provisioning is safe to repeat.
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

    /// <summary>
    /// This network has no broker and no mailpit, so the confirmation e-mail never arrives, and login needs
    /// a confirmed address. The real e-mail journey belongs to the frontend browser suite. Returns the
    /// account id, because registration answers 202 with no body (#860).
    /// </summary>
    public async Task<IdentityId> ConfirmUserEmailAsync(string email)
    {
        ExecResult result = await _postgres.ExecAsync(
        [
            "psql", "-v", "ON_ERROR_STOP=1", "-U", PostgresUser, "-d", AuthDatabaseName, "-q", "-t", "-A",
            "-c", $"UPDATE authsystem.\"Users\" SET \"EmailConfirmed\" = TRUE WHERE \"Email\" = '{email}' RETURNING \"Id\";"
        ]);

        if (result.ExitCode != 0 || !Guid.TryParse(result.Stdout.Trim(), out Guid userId))
        {
            throw new InvalidOperationException(
                $"Failed to confirm user e-mail (exit code {result.ExitCode}).\nStdout:\n{result.Stdout}\nStderr:\n{result.Stderr}");
        }

        return IdentityId.Create(userId);
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
        // One-shot, exactly like the compose migrator, with only the connection strings.
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

    private async Task StartApiContainersAsync()
    {
        // auth-api first, because tms-api fetches its JWKS from it and the seeded admin must be live
        // before any test logs in.
        _authApi = new ContainerBuilder(_worktree.Image(AuthRepository))
            .WithNetwork(_network)
            .WithNetworkAliases("auth-api")
            .WithPortBinding(8080, true)
            .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Testing")
            .WithEnvironment("ASPNETCORE_URLS", "http://+:8080")
            .WithEnvironment("ConnectionStrings__AuthDatabase", AuthConnectionString)
            // The token issuer; tms-api also fetches its JWKS from this address.
            .WithEnvironment("OpenIddict__Issuer", "http://auth-api:8080")
            .WithEnvironment("OpenIddict__ApiClientSecret", ApiClientSecret)
            // Testing skips the startup check for these, but the seeder registers them, and the first
            // post-logout address is the website root the account pages link to.
            .WithEnvironment("OpenIddict__WebClient__RedirectUris__0", "http://localhost:8080/callback")
            .WithEnvironment("OpenIddict__WebClient__PostLogoutRedirectUris__0", "http://localhost:8080")
            .WithEnvironment("AdminUser__Username", AdminUsername)
            .WithEnvironment("AdminUser__Email", AdminEmail)
            .WithEnvironment("AdminUser__Password", AdminPassword)
            // No mail is ever delivered here (see ConfirmUserEmailAsync). These values only satisfy
            // EmailOptionsValidator, which needs a sender identity (M6-06).
            .WithEnvironment("Email__SenderEmail", "noreply@lotro-translator.pl")
            .WithEnvironment("Email__Sender", "lotro-translator.pl")
            .WithEnvironment("Email__Host", "localhost")
            .WithEnvironment("Email__Port", "2525")
            // No broker either; these values only satisfy RabbitMqOptionsValidator.
            .WithEnvironment("RabbitMq__Host", "localhost")
            .WithEnvironment("RabbitMq__Username", "rabbitmq")
            .WithEnvironment("RabbitMq__Password", "changeme")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(request => request.ForPath("/health/live").ForPort(8080)))
            .Build();

        await _authApi.StartAsync();

        _tmsApi = new ContainerBuilder(_worktree.Image(TmsRepository))
            .WithNetwork(_network)
            .WithNetworkAliases("tms-api")
            .WithPortBinding(8080, true)
            .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
            .WithEnvironment("ASPNETCORE_URLS", "http://+:8080")
            .WithEnvironment("ConnectionStrings__TranslationDatabase", TranslationConnectionString)
            .WithEnvironment("Auth__Issuer", "http://auth-api:8080")
            .WithEnvironment("Auth__Authority", "http://auth-api:8080")
            .WithEnvironment("Auth__Audience", Audience)
            // Short, so the background rebuild converges fast (PERF-04).
            .WithEnvironment("TranslationFileRebuild__DebounceWindow", "00:00:00.100")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(request => request.ForPath("/health/live").ForPort(8080)))
            .Build();

        await _tmsApi.StartAsync();
    }

    private static string BuildInternalConnectionString(string database) =>
        $"Host=postgres;Port=5432;Database={database};Username={PostgresUser};Password={PostgresPassword}";

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
