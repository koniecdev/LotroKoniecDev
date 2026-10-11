using Npgsql;

namespace LotroKoniecDev.AuthSystem.Persistence.Settings;

/// <summary>
/// The sign-in server waits at most 20 seconds to open a database connection, whatever the configured
/// connection string says, so no box <c>.env</c> needs a change (#923). The website stops waiting for the
/// sign-in server after 30 seconds, and the sign-in server must give up first. Otherwise it could finish a
/// login renewal that the website never reads, and the website's next try would send a refresh token that
/// is already spent.
/// </summary>
public static class AuthDatabaseConnectionString
{
    public const int OpenTimeoutSeconds = 20;

    public static string WithOpenTimeout(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Timeout = OpenTimeoutSeconds }.ConnectionString;
}
