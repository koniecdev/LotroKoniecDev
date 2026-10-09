namespace LotroKoniecDev.TranslationSystem.API.Messaging;

/// <summary>
/// The broker the AuthSystem already uses. The TMS only consumes from it, the account events of
/// ADR-0065, so it reads the same <c>RabbitMq</c> section the AuthSystem reads and compose hands both
/// services the same <c>RABBITMQ_PASSWORD</c>.
/// </summary>
internal sealed class RabbitMqSettings
{
    public const string ConfigurationSection = "RabbitMq";

    public required string Host { get; init; }
    public int Port { get; init; } = 5672;
    public required string Username { get; init; }
    public required string Password { get; init; }
    public string VirtualHost { get; init; } = "/";
}
