namespace LotroKoniecDev.AuthSystem.Infrastructure.Messaging;

public sealed class RabbitMqOptions
{
    internal const string ConfigurationSection = "RabbitMq";
    public required string Host { get; init; }
    public int Port { get; init; } = 5672;
    public required string Username { get; init; }
    public required string Password { get; init; }
    public string VirtualHost { get; init; } = "/";
}
