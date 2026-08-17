using System.Globalization;
using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Domain.Models;

namespace LotroKoniecDev.Infrastructure.Storage;

/// <summary>
/// The write ledger of ADR-0047 §4, stored as <c>&lt;translation file&gt;.ledger</c> next to the
/// <c>.etag</c>/<c>.endpoint</c> sidecars: one <c>file_id||gossip_id||digest</c> line per fragment
/// this patcher has written, where the digest is the export form of what it left there.
/// </summary>
/// <remarks>
/// The format is deliberately the flat, comment-free cousin of the translation file: three fields,
/// none of which can contain a <c>|</c>, so it can be split rather than carved (ADR-0042 exists
/// because translated content can hold the separator — a digest cannot). A malformed line is
/// dropped rather than failing the read: the ledger is a hint, and losing an entry under-patches
/// while refusing to patch at all would take the launch down.
/// </remarks>
public sealed class TranslationLedger : ITranslationLedger
{
    private const string FieldSeparator = "||";
    private const string TempSuffix = ".tmp";
    private const int FieldCount = 3;

    public IReadOnlyDictionary<LedgerKey, string> Read(string translationFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(translationFilePath);

        string ledgerPath = LedgerPath(translationFilePath);

        if (!File.Exists(ledgerPath))
        {
            return new Dictionary<LedgerKey, string>();
        }

        Dictionary<LedgerKey, string> entries = [];

        try
        {
            foreach (string line in File.ReadLines(ledgerPath))
            {
                if (TryParseEntry(line, out LedgerKey key, out string? digest))
                {
                    entries[key] = digest;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable ledger is an empty ledger (ADR-0047 §4): rows holding English or the
            // current translation still pass the guard, rows holding an older Polish get skipped
            // with a warning. Failing toward English is the correct side, and it must not take the
            // launch down — the same reasoning as the cache sidecars'.
            return new Dictionary<LedgerKey, string>();
        }

        return entries;
    }

    public Result Save(string translationFilePath, IReadOnlyDictionary<LedgerKey, string> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(translationFilePath);
        ArgumentNullException.ThrowIfNull(entries);

        string ledgerPath = LedgerPath(translationFilePath);
        string tempPath = ledgerPath + TempSuffix;

        try
        {
            string? directory = Path.GetDirectoryName(ledgerPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllLines(tempPath, entries.Select(FormatEntry));

            // Atomic swap: a crash between the two leaves either the previous ledger or the new
            // one, never a truncated file the next run would read as a wrong set of entries.
            File.Move(tempPath, ledgerPath, overwrite: true);

            return Result.Success();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Failure(DomainErrors.TranslationFileSync.CacheWriteError(ledgerPath, ex.Message));
        }
    }

    private static string FormatEntry(KeyValuePair<LedgerKey, string> entry)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{entry.Key.FileId}{FieldSeparator}{entry.Key.GossipId}{FieldSeparator}{entry.Value}");

    private static bool TryParseEntry(string line, out LedgerKey key, out string digest)
    {
        key = default;
        digest = string.Empty;

        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        string[] fields = line.Split(FieldSeparator);
        if (fields.Length != FieldCount)
        {
            return false;
        }

        if (!int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int fileId) ||
            !ulong.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong gossipId) ||
            !SourceDigest.IsWireForm(fields[2]))
        {
            return false;
        }

        key = new LedgerKey(fileId, gossipId);
        digest = fields[2];
        return true;
    }

    private static string LedgerPath(string translationFilePath) => translationFilePath + ".ledger";
}
