using System.Globalization;
using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Domain.Models;

namespace LotroKoniecDev.Infrastructure.Storage;

/// <summary>
/// The iteration snapshot of spec 0012 Tier 0, stored as <c>&lt;translation file&gt;.iterations</c>
/// next to the ledger. It holds one <c>file_id||iteration</c> line per text subfile, in file id order.
/// </summary>
/// <remarks>
/// Unlike the ledger, one broken line makes the whole file unreadable. The file is always written in
/// one step, so a broken line means it is not the file we wrote, and its other lines cannot be
/// trusted either. An unreadable snapshot turns detection off, with a warning on every launch, until
/// the next patch saves a new one. A wrong one could report subfiles nobody replaced and force a
/// repair, and spec 0012 asks for no forced repair from a snapshot we cannot trust.
/// </remarks>
public sealed class SubFileIterationSnapshotStore : ISubFileIterationSnapshotStore
{
    private const string FieldSeparator = "||";
    private const string TempSuffix = ".tmp";
    private const int FieldCount = 2;

    public Result<SubFileIterationSnapshot?> Read(string translationFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(translationFilePath);

        string snapshotPath = SnapshotPath(translationFilePath);

        if (!File.Exists(snapshotPath))
        {
            return Result.Success<SubFileIterationSnapshot?>(null);
        }

        Dictionary<int, int> iterations = [];
        int lineNumber = 0;

        try
        {
            foreach (string line in File.ReadLines(snapshotPath))
            {
                lineNumber++;

                if (!TryParseEntry(line, out int fileId, out int iteration) || !iterations.TryAdd(fileId, iteration))
                {
                    return Result.Failure<SubFileIterationSnapshot?>(DomainErrors.IterationSnapshot.ReadError(
                        snapshotPath,
                        $"line {lineNumber} is not a 'file_id||iteration' entry for a text subfile with a file id of its own"));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Failure<SubFileIterationSnapshot?>(
                DomainErrors.IterationSnapshot.ReadError(snapshotPath, ex.Message));
        }

        return Result.Success<SubFileIterationSnapshot?>(SubFileIterationSnapshot.FromIterations(iterations));
    }

    public Result Save(string translationFilePath, SubFileIterationSnapshot snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(translationFilePath);
        ArgumentNullException.ThrowIfNull(snapshot);

        string snapshotPath = SnapshotPath(translationFilePath);
        string tempPath = snapshotPath + TempSuffix;

        try
        {
            string? directory = Path.GetDirectoryName(snapshotPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllLines(tempPath, snapshot.Iterations.OrderBy(entry => entry.Key).Select(FormatEntry));

            // Swap the file in one step. A crash leaves either the old snapshot or the new one, never a
            // half-written file the next launch would compare the DAT with.
            File.Move(tempPath, snapshotPath, overwrite: true);

            return Result.Success();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Failure(DomainErrors.IterationSnapshot.WriteError(snapshotPath, ex.Message));
        }
    }

    private static string FormatEntry(KeyValuePair<int, int> entry)
        => string.Create(CultureInfo.InvariantCulture, $"{entry.Key}{FieldSeparator}{entry.Value}");

    private static bool TryParseEntry(string line, out int fileId, out int iteration)
    {
        fileId = 0;
        iteration = 0;

        string[] fields = line.Split(FieldSeparator);

        // The iteration is a signed int from the DAT, so a minus sign is ours too. Every file id we
        // write belongs to a text subfile.
        return fields.Length == FieldCount
            && int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out fileId)
            && SubFile.IsTextFile(fileId)
            && int.TryParse(fields[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out iteration);
    }

    private static string SnapshotPath(string translationFilePath) => translationFilePath + ".iterations";
}
