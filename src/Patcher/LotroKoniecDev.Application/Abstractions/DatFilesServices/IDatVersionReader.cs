using LotroKoniecDev.Domain.Models;

namespace LotroKoniecDev.Application.Abstractions.DatFilesServices;

public interface IDatVersionReader
{
    Result<DatVersionInfo> ReadVersion(string datFilePath);
}
