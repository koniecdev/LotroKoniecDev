namespace LotroKoniecDev.AuthSystem.Infrastructure.Emails;

public sealed class EmailOptions
{
    internal const string ConfigurationSection = "Email";
    public required string SenderEmail { get; init; }
    public required string Sender { get; init; }
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required EmailSecurityMode Mode { get; init; }
    public int TimeoutSeconds { get; init; } = 10;
    public int MaxSendAttempts { get; init; } = 3;
    public string? Username { get; init; }
    public string? Password { get; init; }
}
