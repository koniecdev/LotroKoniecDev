using LotroKoniecDev.Application.Features.GameLaunching;
using LotroKoniecDev.Domain.Core.Monads;

namespace LotroKoniecDev.Infrastructure.GameLaunching;

public sealed class GameLauncher : IGameLauncher
{
    public Result<int> Launch(string datFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datFilePath);
        
        
        
        return Result.Success(0);
    }
}
