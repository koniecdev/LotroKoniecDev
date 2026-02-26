using System.Text;
using FluentValidation;
using FluentValidation.Results;
using LotroKoniecDev.Application.Abstractions.DatFilesServices;
using LotroKoniecDev.Application.Extensions;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Domain.Models;
using LotroKoniecDev.Primitives.Constants;
using Mediator;

namespace LotroKoniecDev.Application.Features.Export;

internal sealed class ExportTextsQueryHandler : IQueryHandler<ExportTextsQuery, Result<ExportSummaryResponse>>
{
    private const int ProgressReportInterval = 500;
    private readonly IDatFileHandler _datFileHandler;
    private readonly IProgress<OperationProgress> _progressReporter;

    public ExportTextsQueryHandler(
        IDatFileHandler datFileHandler,
        IProgress<OperationProgress> progressReporter)
    {
        _datFileHandler = datFileHandler;
        _progressReporter = progressReporter;
    }
    
    public async ValueTask<Result<ExportSummaryResponse>> Handle(ExportTextsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        
        Result<int> openResult = _datFileHandler.Open(query.DatFilePath);
        if (openResult.IsFailure)
        {
            return Result.Failure<ExportSummaryResponse>(openResult.Error);
        }
        
        int handle = openResult.Value;
        
        try
        {
            Dictionary<int, (int Size, int Iteration)> fileSizes = _datFileHandler.GetAllSubfileSizes(handle);
            int totalTextFiles = fileSizes.Count(kvp => SubFile.IsTextFile(kvp.Key));

            await using StreamWriter writer = new(query.OutputPath, append: false, Encoding.UTF8);
            await WriteHeaderAsync(writer);

            int processedFiles = 0;
            int totalFragments = 0;

            foreach ((int fileId, (int size, _)) in fileSizes)
            {
                if (!SubFile.IsTextFile(fileId))
                {
                    continue;
                }            
                
                Result<SubFile> loadResult = _datFileHandler.LoadSubFile(handle, fileId, size);
                if (loadResult.IsSuccess)
                {
                    SubFile subFile = loadResult.Value;
                    int fragmentCount = 0;

                    foreach ((ulong fragmentId, Fragment fragment) in subFile.Fragments)
                    {
                        string text = string.Join(DatFileConstants.PieceSeparator, fragment.Pieces);

                        // Escape newlines for single-line storage
                        text = text.Replace("\r", "\\r").Replace("\n", "\\n");

                        // Generate default args_order and args_id if fragment has arguments
                        string argsOrder = "NULL";
                        string argsId = "NULL";

                        if (fragment.HasArguments)
                        {
                            IEnumerable<string> order = Enumerable
                                .Range(1, fragment.ArgRefs.Count)
                                .Select(x => x.ToString());

                            argsOrder = string.Join("-", order);
                            argsId = argsOrder; // Default: same order
                        }

                        await writer.WriteLineAsync($"{fileId}||{fragmentId}||{text}||{argsOrder}||{argsId}||1");

                        fragmentCount++;
                    }

                    totalFragments += fragmentCount;
                }

                processedFiles++;

                if (processedFiles % ProgressReportInterval == 0)
                {
                    _progressReporter.Report(new OperationProgress(processedFiles, totalTextFiles));
                }
            }
            
            return Result.Success(new ExportSummaryResponse(
                processedFiles,
                totalFragments,
                query.OutputPath));
        }
        catch (Exception ex)
        {
            return Result.Failure<ExportSummaryResponse>(
                DomainErrors.Export.CannotCreateOutputFile(query.OutputPath, ex.Message));
        }
        finally
        {
            _datFileHandler.Close(handle);
        }
        
    }
    
    private static async Task WriteHeaderAsync(StreamWriter writer)
    {
        await writer.WriteLineAsync("# LOTRO Text Export - Ready for Translation");
        await writer.WriteLineAsync("# Format: file_id||gossip_id||text||args_order||args_id||approved");
        await writer.WriteLineAsync("#");
        await writer.WriteLineAsync("# Translation instructions:");
        await writer.WriteLineAsync("#   1. Replace English text with Polish translation");
        await writer.WriteLineAsync("#   2. DO NOT modify <--DO_NOT_TOUCH!--> markers - they are variable placeholders");
        await writer.WriteLineAsync("#   3. args_order/args_id - leave as NULL unless changing argument order");
        await writer.WriteLineAsync("#   4. Remove lines you don't translate (or leave them - identical lines are ignored)");
        await writer.WriteLineAsync("#");
    }
}
