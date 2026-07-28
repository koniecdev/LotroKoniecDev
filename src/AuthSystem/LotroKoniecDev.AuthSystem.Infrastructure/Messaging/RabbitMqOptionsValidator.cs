using FluentValidation;

namespace LotroKoniecDev.AuthSystem.Infrastructure.Messaging;

public sealed class RabbitMqOptionsValidator : AbstractValidator<RabbitMqOptions>
{
    public RabbitMqOptionsValidator()
    {
        RuleFor(x => x.Host)
            .NotEmpty()
            .WithMessage(
                $"{RabbitMqOptions.ConfigurationSection}:{nameof(RabbitMqOptions.Host)} (the broker host) is required. "
                + $"Inject it via the {RabbitMqOptions.ConfigurationSection}__{nameof(RabbitMqOptions.Host)} environment variable.");

        RuleFor(x => x.Port)
            .InclusiveBetween(1, 65535)
            .WithMessage($"{nameof(RabbitMqOptions.Port)} must be between 1 and 65535");

        RuleFor(x => x.Username)
            .NotEmpty()
            .WithMessage($"{nameof(RabbitMqOptions.Username)} is required");

        RuleFor(x => x.Password)
            .NotEmpty()
            .WithMessage($"{nameof(RabbitMqOptions.Password)} is required");

        RuleFor(x => x.VirtualHost)
            .NotEmpty()
            .WithMessage($"{nameof(RabbitMqOptions.VirtualHost)} is required (use \"/\" for the default virtual host)");
    }
}
