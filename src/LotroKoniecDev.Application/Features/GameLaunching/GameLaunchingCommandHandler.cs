using LotroKoniecDev.Application.Abstractions;
using Mediator;

namespace LotroKoniecDev.Application.Features.GameLaunching;

internal sealed class GameLaunchingCommandHandler : ICommandHandler<GameLaunchingCommand, Result<GameLaunchingResponse>>
{
    private readonly IGameLaunchingStrategy _strategy;

    public GameLaunchingCommandHandler(IGameLaunchingStrategy strategy)
    {
        _strategy = strategy;
    }

    public ValueTask<Result<GameLaunchingResponse>> Handle(
        GameLaunchingCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return _strategy.ExecuteAsync(command, cancellationToken);
    }
}
