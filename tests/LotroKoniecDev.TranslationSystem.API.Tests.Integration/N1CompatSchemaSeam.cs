using System.Text.RegularExpressions;
using DotNet.Testcontainers.Containers;
using Npgsql;
using Testcontainers.PostgreSql;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration;

/// <summary>
/// N-1 backward-compatibility seam (ADR-0024 / #340). When <see cref="SchemaScriptsDirEnvVar"/>
/// is set — only ever by <c>scripts/n1-compat.sh</c> — the factory applies a pre-generated
/// idempotent HEAD schema script to its fresh PostgreSQL container before its own
/// <c>MigrateAsync()</c>, which then no-ops against the already-filled
/// <c>__EFMigrationsHistory</c>. This (older) suite then runs against the newer schema.
/// Unset, every code path here is skipped and normal test runs are unchanged.
/// Every misconfiguration throws: a vacuous N-1 check must read as red, never as green.
/// Twin copy: <c>tests/LotroKoniecDev.AuthSystem.API.Tests.Integration/N1CompatSchemaSeam.cs</c>
/// — keep in sync.
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
