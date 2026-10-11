using Npgsql;

namespace LotroKoniecDev.AuthSystem.Persistence.Settings;

/// <summary>
/// The sign-in server gives up opening a database connection after 20 seconds, whatever the configured
/// connection string says, so no box <c>.env</c> needs a change (#923). The website waits 30 seconds for
/// the sign-in server, and the sign-in server has to give up first. Otherwise it could finish a login
/// renewal that the website never reads, and the website's next try would send a refresh token that is
/// already spent. The website tries a renewal only once for the same reason. This limit covers opening a
/// connection only. Retries and slow queries can still run past the website's 30 seconds (#1121).
/// </summary>
public static class AuthDatabaseConnectionString
{
    private const int OpenTimeoutSeconds = 20;

    public static string WithOpenTimeout(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Timeout = OpenTimeoutSeconds }.ConnectionString;
}
