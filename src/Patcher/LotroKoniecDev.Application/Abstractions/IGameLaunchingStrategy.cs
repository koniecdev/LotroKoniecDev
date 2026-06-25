using LotroKoniecDev.Application.Features.GameLaunching;

namespace LotroKoniecDev.Application.Abstractions;

public interface IGameLaunchingStrategy
{
    ValueTask<Result<GameLaunchingResponse>> ExecuteAsync(
        GameLaunchingCommand command, CancellationToken cancellationToken);
}
