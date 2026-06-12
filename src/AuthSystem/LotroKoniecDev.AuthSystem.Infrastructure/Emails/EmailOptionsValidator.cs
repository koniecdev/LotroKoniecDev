using FluentValidation;
using LotroKoniecDev.SharedKernel.Constants;

namespace LotroKoniecDev.AuthSystem.Infrastructure.Emails;

public sealed class EmailOptionsValidator : AbstractValidator<EmailOptions>
{
    public EmailOptionsValidator()
    {
        RuleFor(x => x.SenderEmail)
            .NotEmpty()
            .WithMessage($"{nameof(EmailOptions.SenderEmail)} is required")
            .MinimumLength(EmailConstants.MinLength)
            .WithMessage($"{nameof(EmailOptions.SenderEmail)} must be at least {EmailConstants.MinLength} characters long")
            .MaximumLength(EmailConstants.MaxLength)
            .WithMessage($"{nameof(EmailOptions.SenderEmail)} must not exceed {EmailConstants.MaxLength} characters")
            .Matches(EmailConstants.RegexPattern)
            .WithMessage($"{nameof(EmailOptions.SenderEmail)} must be a valid email address");

        RuleFor(x => x.Sender)
            .NotEmpty()
            .WithMessage($"{nameof(EmailOptions.Sender)} is required");

        RuleFor(x => x.Host)
            .NotEmpty()
            .WithMessage($"{nameof(EmailOptions.Host)} is required");

        RuleFor(x => x.Port)
            .InclusiveBetween(1, 65535)
            .WithMessage($"{nameof(EmailOptions.Port)} must be between 1 and 65535");

        RuleFor(x => x.Mode)
            .IsInEnum()
            .WithMessage($"{nameof(EmailOptions.Mode)} must be a defined {nameof(EmailSecurityMode)} value");

        RuleFor(x => x.TimeoutSeconds)
            .InclusiveBetween(1, 120)
            .WithMessage($"{nameof(EmailOptions.TimeoutSeconds)} must be between 1 and 120");

        RuleFor(x => x.MaxSendAttempts)
            .InclusiveBetween(1, 10)
            .WithMessage($"{nameof(EmailOptions.MaxSendAttempts)} must be between 1 and 10");

        When(x => !string.IsNullOrWhiteSpace(x.Username), () =>
        {
            RuleFor(x => x.Password)
                .NotEmpty()
                .WithMessage($"{nameof(EmailOptions.Password)} is required when {nameof(EmailOptions.Username)} is provided");
        });
    }
}
