using Npgsql;

namespace LotroKoniecDev.AuthSystem.Persistence.Settings;

/// <summary>
/// The sign-in server waits at most 20 seconds to open a database connection, whatever the connection
/// string says (#923). The website waits 30 seconds for the sign-in server, so the server must give up
/// first. Otherwise it could finish a login renewal the website never reads, and the website's next try
/// would send a refresh token that is already spent. This covers opening a connection only: retries and
/// slow queries can still run past 30 seconds (#1121).
/// </summary>
public static class AuthDatabaseConnectionString
{
    private const int MaxOpenTimeoutSeconds = 20;

    public static string CapOpenTimeout(string connectionString)
    {
        NpgsqlConnectionStringBuilder builder =
            ConnectionStringReader.Read(connectionString, ConnectionStringSettings.AuthDatabaseKey);

        // Npgsql reads Timeout=0 as "wait forever".
        if (builder.Timeout is <= 0 or > MaxOpenTimeoutSeconds)
        {
            builder.Timeout = MaxOpenTimeoutSeconds;
        }

        return builder.ConnectionString;
    }
}
