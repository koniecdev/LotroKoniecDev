namespace LotroKoniecDev.TranslationSystem.API.Messaging;

/// <summary>
/// The AuthSystem's broker: the same <c>RabbitMq</c> section, and compose hands both services the same
/// <c>RABBITMQ_PASSWORD</c> (ADR-0065).
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
