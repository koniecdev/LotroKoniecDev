using LotroKoniecDev.Application.Abstractions;
using Mediator;

namespace LotroKoniecDev.Application.Features.GameLaunching;

internal sealed class GameLaunchingCommandHandler : ICommandHandler<GameLaunchingCommand, Result<GameLaunchingResponse>>
{
    private readonly LegacyGameLaunchingStrategy _legacy;
    private readonly SimplifiedGameLaunchingStrategy _simplified;

    public GameLaunchingCommandHandler(
        LegacyGameLaunchingStrategy legacy,
        SimplifiedGameLaunchingStrategy simplified)
    {
        _legacy = legacy;
        _simplified = simplified;
    }

    public async ValueTask<Result<GameLaunchingResponse>> Handle(
        GameLaunchingCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        IGameLaunchingStrategy strategy = command.UseLegacyFlow
            ? _legacy
            : _simplified;

        return await strategy.ExecuteAsync(command, cancellationToken);
    }
}
