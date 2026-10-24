using System.Diagnostics.CodeAnalysis;
using Npgsql;

namespace LotroKoniecDev.AuthSystem.Persistence.Settings;

/// <summary>
/// Reads a connection string and keeps Npgsql's own error out of the boot log (#1157). That error names
/// the part Npgsql could not use: a piece of an unquoted password that contains <c>;</c>, or a whole
/// value typed after a wrong key.
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
        // Npgsql 10 throws an ArgumentException for most bad strings, but a bare KeyNotFoundException for an
        // unknown key with an empty value. The parse has no side effects, so every failure means unreadable.
        catch (Exception)
        {
            builder = null;
            return false;
        }
    }
}
