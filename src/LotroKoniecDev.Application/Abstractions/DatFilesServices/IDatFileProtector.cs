using LotroKoniecDev.Domain.Core.Monads;

namespace LotroKoniecDev.Application.Abstractions.DatFilesServices;

public interface IDatFileProtector
{
    Result Protect(string datFilePath);
    Result Unprotect(string datFilePath);
    bool IsProtected(string datFilePath);
}
