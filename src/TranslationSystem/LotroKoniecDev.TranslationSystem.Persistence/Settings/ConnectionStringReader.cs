using System.Diagnostics.CodeAnalysis;
using Npgsql;

namespace LotroKoniecDev.TranslationSystem.Persistence.Settings;

/// <summary>
/// Keeps Npgsql's own parse error out of the boot log (#1157, ADR-0067 decision 4): it names the part it
/// could not use, which can be a piece of an unquoted password with a <c>;</c>, or a value typed after
/// the wrong key.
/// </summary>
internal static class ConnectionStringReader
{
    public static bool IsReadable(string connectionString) => TryRead(connectionString, out _);

    public static NpgsqlConnectionStringBuilder Read(string connectionString, string settingName)
    {
        if (TryRead(connectionString, out NpgsqlConnectionStringBuilder? builder))
        {
            return builder;
        }

        throw new InvalidOperationException(
            $"{settingName} is not a valid connection string. It takes Key=Value pairs separated by ';', "
            + "and a value that contains ';' goes in double quotes.");
    }

    private static bool TryRead(
        string connectionString,
        [NotNullWhen(true)] out NpgsqlConnectionStringBuilder? builder)
    {
        try
        {
            builder = new NpgsqlConnectionStringBuilder(connectionString);
            return true;
        }
        // Npgsql 10 also throws a bare KeyNotFoundException for an unknown key with an empty value. The
        // parse has no side effects, so every failure means unreadable.
        catch (Exception)
        {
            builder = null;
            return false;
        }
    }
}
