using System.Text.RegularExpressions;
using DotNet.Testcontainers.Containers;
using Npgsql;
using Testcontainers.PostgreSql;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration;

/// <summary>
/// The hook for the N-1 compatibility check (ADR-0024, #340). When
/// <see cref="SchemaScriptsDirEnvVar"/> is set, which only <c>scripts/n1-compat.sh</c> ever does, the
/// factory runs a prepared HEAD schema script against its fresh PostgreSQL container before its own
/// <c>MigrateAsync()</c>. That call then does nothing, because <c>__EFMigrationsHistory</c> is already
/// filled, and this older suite runs against the newer schema.
/// When the variable is not set, none of this runs and a normal test run is unchanged.
/// Every misconfiguration throws: an N-1 check that tested nothing must show up as red, never as green.
/// There is a twin copy in
/// <c>tests/LotroKoniecDev.AuthSystem.API.Tests.Integration/N1CompatSchemaSeam.cs</c>. Keep the two in
/// sync.
/// </summary>
internal static partial class N1CompatSchemaSeam
{
    public const string SchemaScriptsDirEnvVar = "N1_COMPAT_SCHEMA_SCRIPTS_DIR";

    public static async Task ApplyIfConfiguredAsync(PostgreSqlContainer postgresContainer, string scriptFileName)
    {
        string? scriptsDirectory = Environment.GetEnvironmentVariable(SchemaScriptsDirEnvVar);
        if (string.IsNullOrWhiteSpace(scriptsDirectory))
        {
            return;
        }

        string scriptPath = Path.Combine(scriptsDirectory, scriptFileName);
        if (!File.Exists(scriptPath))
        {
            throw new InvalidOperationException(
                $"{SchemaScriptsDirEnvVar} is set but '{scriptPath}' does not exist — refusing to run a vacuous N-1 check.");
        }

        string script = await File.ReadAllTextAsync(scriptPath);

        IReadOnlyCollection<HistoryInsert> expectedInserts = ParseHistoryInserts(script);
        if (expectedInserts.Count == 0)
        {
            throw new InvalidOperationException(
                $"'{scriptPath}' inserts no __EFMigrationsHistory rows — an empty or malformed schema script would leave this suite migrating its own (old) schema.");
        }

        ExecResult execResult = await postgresContainer.ExecScriptAsync("\\set ON_ERROR_STOP on\n" + script);
        if (execResult.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Applying '{scriptPath}' failed (psql exit code {execResult.ExitCode}). Stderr: {execResult.Stderr}");
        }

        await VerifyHistoryContainsAsync(postgresContainer.GetConnectionString(), expectedInserts);
    }

    internal static IReadOnlyCollection<HistoryInsert> ParseHistoryInserts(string script)
    {
        return HistoryInsertRegex()
            .Matches(script)
            .Select(match => new HistoryInsert(match.Groups["table"].Value, match.Groups["id"].Value))
            .Distinct()
            .ToArray();
    }

    private static async Task VerifyHistoryContainsAsync(
        string connectionString,
        IReadOnlyCollection<HistoryInsert> expectedInserts)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();

        foreach (IGrouping<string, HistoryInsert> tableGroup in expectedInserts.GroupBy(insert => insert.HistoryTable))
        {
            HashSet<string> appliedIds = [];

            // The identifier is not parameterizable; it is safe to interpolate because the regex
            // only ever captures an optional plain-identifier schema plus the fixed quoted table name.
            await using NpgsqlCommand command = new($"SELECT \"MigrationId\" FROM {tableGroup.Key}", connection);
            await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    appliedIds.Add(reader.GetString(0));
                }
            }

            string[] missingIds = tableGroup
                .Select(insert => insert.MigrationId)
                .Where(migrationId => !appliedIds.Contains(migrationId))
                .ToArray();

            if (missingIds.Length > 0)
            {
                throw new InvalidOperationException(
                    $"The schema script ran, but {tableGroup.Key} is missing migration(s): {string.Join(", ", missingIds)} — the script did not apply fully.");
            }
        }
    }

    [GeneratedRegex(
        @"INSERT INTO (?<table>(?:[A-Za-z_][A-Za-z0-9_]*\.)?""__EFMigrationsHistory"")\s*\(\s*""MigrationId"",\s*""ProductVersion""\s*\)\s*VALUES\s*\('(?<id>[^']+)'",
        RegexOptions.CultureInvariant)]
    private static partial Regex HistoryInsertRegex();
}

internal sealed record HistoryInsert(string HistoryTable, string MigrationId);
