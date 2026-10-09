using FluentValidation;

namespace LotroKoniecDev.TranslationSystem.API.Messaging;

/// <summary>
/// Stops the boot when the broker settings are missing. Without them the account events never arrive,
/// and nothing but a stale name in the editor would show it.
/// </summary>
internal sealed class RabbitMqSettingsValidator : AbstractValidator<RabbitMqSettings>
{
    public RabbitMqSettingsValidator()
    {
        RuleFor(x => x.Host)
            .NotEmpty()
            .WithMessage(
                $"{KeyPath(nameof(RabbitMqSettings.Host))} (the broker host) is required. "
                + $"Inject it via the {RabbitMqSettings.ConfigurationSection}__{nameof(RabbitMqSettings.Host)} environment variable.");

        RuleFor(x => x.Port)
            .InclusiveBetween(1, 65535)
            .WithMessage($"{KeyPath(nameof(RabbitMqSettings.Port))} must be between 1 and 65535.");

        RuleFor(x => x.Username)
            .NotEmpty()
            .WithMessage($"{KeyPath(nameof(RabbitMqSettings.Username))} is required.");

        RuleFor(x => x.Password)
            .NotEmpty()
            .WithMessage($"{KeyPath(nameof(RabbitMqSettings.Password))} is required.");

        RuleFor(x => x.VirtualHost)
            .NotEmpty()
            .WithMessage($"{KeyPath(nameof(RabbitMqSettings.VirtualHost))} is required (use \"/\" for the default virtual host).");
    }

    private static string KeyPath(string propertyName)
        => $"{RabbitMqSettings.ConfigurationSection}:{propertyName}";
}
