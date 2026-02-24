using LotroKoniecDev.Domain.Core.Monads;

namespace LotroKoniecDev.Application.Features.DatVersionReading;

public interface IDatVersionReader
{
    Result<DatVersionInfo> ReadVersion(string datFilePath);
}
