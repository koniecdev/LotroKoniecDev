using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace LotroKoniecDev.AuthSystem.Persistence.DbContexts;

internal sealed class AuthDbContextDesignTimeFactory : IDesignTimeDbContextFactory<AuthDbContext>
{
    public AuthDbContext CreateDbContext(string[] args)
    {
        string? connectionStringOverride = GetArgumentValue(args, "--connection");

        string connectionString = !string.IsNullOrWhiteSpace(connectionStringOverride)
            ? connectionStringOverride
            : BuildConnectionStringFromConfiguration();

        DbContextOptionsBuilder<AuthDbContext> optionsBuilder = new();
        optionsBuilder.UseNpgsql(connectionString, npgsqlOptions =>
        {
            npgsqlOptions.CommandTimeout(300);
            npgsqlOptions.EnableRetryOnFailure(
                maxRetryCount: 3,
                maxRetryDelay: TimeSpan.FromSeconds(10),
                errorCodesToAdd: null);
            npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", DatabaseSchemas.Auth);
        });

        // The OpenIddict tables belong to the AuthDbContext model. Without this call EF Core would not
        // see them and would write migrations that drop those tables.
        optionsBuilder.UseOpenIddict();

        return new AuthDbContext(optionsBuilder.Options);
    }

    private static string BuildConnectionStringFromConfiguration()
    {
        string environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                             ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
                             ?? "Development";

        string basePath = ResolveBasePath();

        IConfigurationRoot configuration = new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .AddJsonFile($"appsettings.{environment}.json", optional: true, reloadOnChange: false)
            .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();

        string? connectionString = configuration["ConnectionStrings:AuthDatabase"];

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Connection string 'ConnectionStrings:AuthDatabase' not found. " +
                $"Searched in: appsettings.json, appsettings.{environment}.json, appsettings.Local.json, environment variables. " +
                $"Base path: {basePath}");
        }

        return connectionString;
    }

    private static string ResolveBasePath()
    {
        string currentDir = Directory.GetCurrentDirectory();

        if (File.Exists(Path.Combine(currentDir, "appsettings.json")))
        {
            return currentDir;
        }

        DirectoryInfo? directory = new(currentDir);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "appsettings.json")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }

        return currentDir;
    }

    private static string? GetArgumentValue(string[] args, string argumentName)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(argumentName, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }
        return null;
    }
}
