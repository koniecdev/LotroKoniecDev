using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Domain.Models;

namespace LotroKoniecDev.Application.Extensions;

public static class DatFileHandlerExtensions
{
    public static Result<SubFile> LoadSubFile(
        this IDatFileHandler handler,
        int handle,
        int fileId,
        int size,
        bool loadVersion = false)
    {
        Result<byte[]> dataResult = handler.GetSubfileData(handle, fileId, size);

        if (dataResult.IsFailure)
        {
            return Result.Failure<SubFile>(dataResult.Error);
        }

        try
        {
            SubFile subFile = new SubFile();

            if (loadVersion)
            {
                subFile.Version = handler.GetSubfileVersion(handle, fileId);
            }

            subFile.Parse(dataResult.Value);
            return Result.Success(subFile);
        }
        catch (Exception ex)
        {
            return Result.Failure<SubFile>(
                DomainErrors.SubFile.ParseError(fileId, ex.Message));
        }
    }
}
