using FluentValidation;
using FluentValidation.Results;
using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Abstractions.Messaging;
using LotroKoniecDev.Application.Extensions;

namespace LotroKoniecDev.Application.Features.GameLaunching;

internal sealed class GameLaunchingCommandHandler : ICommandHandler<GameLaunchingCommand, Result<GameLaunchingResponse>>
{
    private readonly IGameLaunchingStrategy _strategy;
    private readonly IValidator<GameLaunchingCommand> _validator;

    public GameLaunchingCommandHandler(
        IGameLaunchingStrategy strategy,
        IValidator<GameLaunchingCommand> validator)
    {
        _strategy = strategy;
        _validator = validator;
    }

    public ValueTask<Result<GameLaunchingResponse>> Handle(
        GameLaunchingCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        ValidationResult validationResult = _validator.Validate(command);
        if (!validationResult.IsValid)
        {
            return new ValueTask<Result<GameLaunchingResponse>>(
                Result.Failure<GameLaunchingResponse>(validationResult.ToValidationError(nameof(GameLaunchingCommand))));
        }

        return _strategy.ExecuteAsync(command, cancellationToken);
    }
}
