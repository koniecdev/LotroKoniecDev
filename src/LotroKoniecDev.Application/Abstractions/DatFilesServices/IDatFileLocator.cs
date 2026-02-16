using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Domain.Models;

namespace LotroKoniecDev.Application.Abstractions.DatFilesServices;

public interface IDatFileLocator
{
    Result<IReadOnlyList<DatFileLocation>> LocateAll(Action<string>? progress = null);
}
