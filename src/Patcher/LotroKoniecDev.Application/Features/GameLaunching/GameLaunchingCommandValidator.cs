using FluentValidation;

namespace LotroKoniecDev.Application.Features.GameLaunching;

public sealed class GameLaunchingCommandValidator : AbstractValidator<GameLaunchingCommand>
{
    public GameLaunchingCommandValidator()
    {
        RuleFor(x => x.DatFilePath).NotEmpty();
        RuleFor(x => x.GameVersionFilePath).NotEmpty();
        RuleFor(x => x.TranslationFilePath).NotEmpty();
    }
}
