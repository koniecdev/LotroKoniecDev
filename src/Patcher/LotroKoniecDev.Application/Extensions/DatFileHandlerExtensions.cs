using LotroKoniecDev.Application.Abstractions.DatFilesServices;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Models;

namespace LotroKoniecDev.Application.Extensions;

public static class DatFileHandlerExtensions
{
    /// <param name="readOnly">
    /// Set this only when the caller never writes the subfile back. Otherwise the subfile is refused
    /// unless writing it unchanged gives exactly the bytes that were read, so no write can lose data
    /// the parser did not understand (#1194). The check is on by default, so a new writer cannot
    /// forget it.
    /// </param>
    public static Result<SubFile> LoadSubFile(
        this IDatFileHandler handler,
        int handle,
        int fileId,
        int size,
        bool loadVersion = false,
        bool readOnly = false)
    {
        Result<byte[]> dataResult = handler.GetSubfileData(handle, fileId, size);

        if (dataResult.IsFailure)
        {
            return Result.Failure<SubFile>(dataResult.Error);
        }

        try
        {
            SubFile subFile = new();

            if (loadVersion)
            {
                subFile.Version = handler.GetSubfileVersion(handle, fileId);
            }

            subFile.Parse(dataResult.Value);

            if (!readOnly && !subFile.Serialize().AsSpan().SequenceEqual(dataResult.Value))
            {
                return Result.Failure<SubFile>(DomainErrors.SubFile.NotWritableUnchanged(fileId));
            }

            return Result.Success(subFile);
        }
        catch (Exception ex)
        {
            return Result.Failure<SubFile>(
                DomainErrors.SubFile.ParseError(fileId, ex.Message));
        }
    }
}
