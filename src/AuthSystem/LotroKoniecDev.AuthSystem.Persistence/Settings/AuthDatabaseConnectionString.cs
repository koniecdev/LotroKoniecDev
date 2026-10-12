using Npgsql;

namespace LotroKoniecDev.AuthSystem.Persistence.Settings;

/// <summary>
/// The sign-in server waits at most 20 seconds to open a database connection, whatever the configured
/// connection string says, so no box <c>.env</c> needs a change (#923). The website waits 30 seconds for
/// the sign-in server, and the sign-in server has to give up first. Otherwise it could finish a login
/// renewal that the website never reads, and the website's next try, on a later request after a timeout
/// (#1062), would send a refresh token that is already spent. The website sends each renewal only once, with
/// no retry inside the request, for the same reason. This limit covers opening a connection only. Retries
/// and slow queries can still run past the website's 30 seconds (#1121).
/// </summary>
public static class AuthDatabaseConnectionString
{
    private const int MaxOpenTimeoutSeconds = 20;

    public static string CapOpenTimeout(string connectionString)
    {
        NpgsqlConnectionStringBuilder builder = new(connectionString);

        // Npgsql reads Timeout=0 as "wait forever".
        if (builder.Timeout is <= 0 or > MaxOpenTimeoutSeconds)
        {
            builder.Timeout = MaxOpenTimeoutSeconds;
        }

        return builder.ConnectionString;
    }
}
