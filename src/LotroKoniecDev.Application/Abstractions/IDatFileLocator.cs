using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Primitives.Enums;

namespace LotroKoniecDev.Application.Abstractions;

public interface IDatFileLocator
{
    Result<IReadOnlyList<DatFileLocation>> LocateAll(Action<string>? progress = null);
}

public sealed record DatFileLocation(
    string Path,
    DatFileSource Source,
    string DisplayName);
