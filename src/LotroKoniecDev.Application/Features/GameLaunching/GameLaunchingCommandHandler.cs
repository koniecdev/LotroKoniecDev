using LotroKoniecDev.Application.Abstractions;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.Application.Features.GameLaunching;

internal sealed class GameLaunchingCommandHandler : ICommandHandler<GameLaunchingCommand, Result<GameLaunchingResponse>>
{
    private readonly IGameLaunchingStrategy _legacy;
    private readonly IGameLaunchingStrategy _simplified;

    public GameLaunchingCommandHandler(
        [FromKeyedServices("legacy")] IGameLaunchingStrategy legacy,
        [FromKeyedServices("simplified")] IGameLaunchingStrategy simplified)
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
