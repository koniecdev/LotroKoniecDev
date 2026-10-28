using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Abstractions.DatFilesServices;
using LotroKoniecDev.Application.Extensions;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Models;
using LotroKoniecDev.Primitives.Constants;
using LotroKoniecDev.Primitives.Enums;

namespace LotroKoniecDev.Application.Features.Patching;

/// <summary>
/// Writes approved translations into the DAT, one fragment at a time. Every row passes the source
/// guard of ADR-0047 first, so old Polish never lands on English that has changed.
/// </summary>
/// <remarks>
/// Every successful run ends by saving the DAT's iteration snapshot, here and not in the callers, so
/// no path that patches can forget it (spec 0012 Tier 0, "Built" note).
/// </remarks>
internal sealed class PatchingService : IPatchingService
{
    private const int ProgressReportInterval = 1000;

    /// <summary>
    /// The same limit the parser uses. <c>source moved</c> can hit thousands of rows after a big
    /// update (U49 changed 1,644 sources), and <c>no source digest</c> hits every row of a six-column
    /// file. So both are reported as a count plus a few examples instead of one line each.
    /// </summary>
    private const int MaxCollectedGuardWarnings = 100;

    private readonly IDatFileHandler _datFileHandler;
    private readonly ITranslationParser _translationParser;
    private readonly ITranslationLedger _translationLedger;
    private readonly ISubFileIterationSnapshotStore _snapshotStore;

    public PatchingService(
        IDatFileHandler datFileHandler,
        ITranslationParser translationParser,
        ITranslationLedger translationLedger,
        ISubFileIterationSnapshotStore snapshotStore)
    {
        _datFileHandler = datFileHandler;
        _translationParser = translationParser;
        _translationLedger = translationLedger;
        _snapshotStore = snapshotStore;
    }

    public Result<PatchSummaryResponse> ApplyTranslations(
        string translationsPath,
        string datFilePath,
        IProgress<OperationProgress>? progress = null)
    {
        Result<TranslationParseResult> translationParseResult =
            _translationParser.ParseFile(translationsPath);

        if (translationParseResult.IsFailure)
        {
            return Result.Failure<PatchSummaryResponse>(translationParseResult.Error);
        }

        IReadOnlyList<Translation> translations = translationParseResult.Value.Translations;

        // A line the parser rejected is always reported (ADR-0042), together with the per-fragment
        // warnings.
        IReadOnlyList<string> parseWarnings = translationParseResult.Value.Warnings;

        if (translations.Count == 0)
        {
            // The warning list is printed only after a successful patch, so the reason must travel
            // inside the error (ADR-0042).
            return Result.Failure<PatchSummaryResponse>(
                translationParseResult.Value.RejectedLineCount > 0
                    ? DomainErrors.Translation.NoTranslationsEveryLineRejected(
                        translationParseResult.Value.RejectedLineCount,
                        parseWarnings[0])
                    : DomainErrors.Translation.NoTranslations);
        }

        Result<int> datFileOpenResult = _datFileHandler.Open(datFilePath, DatFileAccess.ReadWrite);

        if (datFileOpenResult.IsFailure)
        {
            return Result.Failure<PatchSummaryResponse>(datFileOpenResult.Error);
        }

        int datFileHandle = datFileOpenResult.Value;

        bool datFlushed = false;

        try
        {
            Dictionary<int, (int Size, int Iteration)> fileSizes = _datFileHandler.GetAllSubfileSizes(datFileHandle);

            List<string> warnings = [.. parseWarnings];
            int appliedCount = 0;
            int skippedCount = 0;

            int currentFileId = -1;
            SubFile? currentSubFile = null;
            bool currentSubFileModified = false;

            // What this patcher wrote before, and what it writes now. The set read from disk is
            // updated, never rebuilt (ITranslationLedger, ADR-0047 §4).
            Dictionary<LedgerKey, string> ledgerEntries = new(_translationLedger.Read(translationsPath));
            Dictionary<LedgerKey, string> pendingLedgerEntries = [];
            bool ledgerChanged = false;

            BoundedGuardWarnings sourceMoved = new();
            BoundedGuardWarnings missingSourceDigest = new();

            // A row reaches the DAT only when its whole subfile is written back, so its ledger entry
            // waits until then and is dropped when the write fails (ADR-0047 §4).
            void WriteCurrentSubFile()
            {
                // A subfile where the guard refused every row still holds what the launcher put
                // there, so writing it back would change the archive for nothing. On update day that
                // is most of them, and "skipped" must mean "wrote nothing".
                if (!currentSubFileModified)
                {
                    pendingLedgerEntries.Clear();
                    return;
                }

                byte[] data = currentSubFile!.Serialize();
                Result putSubfileDataResult = _datFileHandler.PutSubfileData(
                    handle: datFileHandle,
                    fileId: currentFileId,
                    data: data,
                    version: currentSubFile.Version,
                    iteration: fileSizes[currentFileId].Iteration);

                if (putSubfileDataResult.IsFailure)
                {
                    warnings.Add(putSubfileDataResult.Error.Message);
                }
                else
                {
                    foreach ((LedgerKey key, string digest) in pendingLedgerEntries)
                    {
                        ledgerChanged |= !ledgerEntries.TryGetValue(key, out string? recorded)
                            || !string.Equals(recorded, digest, StringComparison.Ordinal);
                        ledgerEntries[key] = digest;
                    }
                }

                pendingLedgerEntries.Clear();
            }

            foreach (Translation translation in translations)
            {
                if (!translation.IsApproved)
                {
                    skippedCount++;
                    continue;
                }

                if (!fileSizes.ContainsKey(translation.FileId))
                {
                    warnings.Add($"File {translation.FileId} not found in DAT archive");
                    skippedCount++;
                    continue;
                }

                if (!SubFile.IsTextFile(translation.FileId))
                {
                    warnings.Add($"File {translation.FileId} is not a text file");
                    skippedCount++;
                    continue;
                }

                // Checked before a subfile is loaded, while a row that cannot be written is still only
                // a row (ADR-0043). Only a hand-edited or hostile file has one: the TMS limits the
                // text at the API.
                string[] pieces = translation.GetPieces();

                if (!pieces.All(Fragment.IsWritablePiece))
                {
                    warnings.Add(
                        $"Fragment {translation.GossipId} in file {translation.FileId} has a text piece of "
                        + $"{pieces.Max(piece => piece.Length)} characters, above the "
                        + $"{DatFileConstants.MaxTextPieceLength} the DAT format allows");
                    skippedCount++;
                    continue;
                }

                // Also checked before a subfile is loaded: a six-column file cannot be patched at all
                // (ADR-0047 §3), and deciding it here saves a subfile load for each of about 800k rows.
                if (translation.SourceDigest is null)
                {
                    missingSourceDigest.Add(
                        $"Fragment {translation.GossipId} in file {translation.FileId}: no source digest — "
                        + "the row cannot be verified against the English the DAT holds, so it was not written");
                    skippedCount++;
                    continue;
                }

                if (translation.FileId != currentFileId)
                {
                    if (currentSubFile is not null && currentFileId != -1)
                    {
                        WriteCurrentSubFile();
                    }

                    (int size, int _) = fileSizes[translation.FileId];
                    Result<SubFile> loadResult = _datFileHandler.LoadSubFile(
                        handle: datFileHandle,
                        fileId: translation.FileId,
                        size: size,
                        loadVersion: true);

                    if (loadResult.IsFailure)
                    {
                        warnings.Add(loadResult.Error.Message);
                        currentSubFile = null;
                        currentFileId = -1;
                        skippedCount++;
                        continue;
                    }

                    currentSubFile = loadResult.Value;
                    currentFileId = translation.FileId;
                    currentSubFileModified = false;
                }

                if (currentSubFile is null)
                {
                    continue;
                }

                if (currentSubFile.TryGetFragment(translation.FragmentId, out Fragment? fragment)
                    && fragment is not null)
                {
                    LedgerKey ledgerKey = new(translation.FileId, translation.GossipId);

                    // What the fragment holds now, and what it would hold after the write. Both are in
                    // the export form the digest is defined over (ADR-0047 §3).
                    string currentDigest = SourceDigest.ForFragment(fragment);
                    string writtenDigest = SourceDigest.ForExportForm(translation.Content, fragment.ArgRefs.Count);

                    bool admitted =
                        // (a) the English this translation was made for, untouched or put back by the
                        //     launcher (the repair paths).
                        SourceDigest.Matches(currentDigest, translation.SourceDigest)
                        // (b) what this patcher last wrote there, so a newer translation of the same
                        //     English can land on a fragment that already holds Polish. Pending
                        //     entries of the subfile in memory count too: a hand-made file can list a
                        //     key twice, and the second row must see the first row's write.
                        || (pendingLedgerEntries.TryGetValue(ledgerKey, out string? recordedDigest)
                                || ledgerEntries.TryGetValue(ledgerKey, out recordedDigest))
                            && SourceDigest.Matches(currentDigest, recordedDigest)
                        // (c) what this row would write. The write changes nothing, only our own patch
                        //     could have put that text there, and it fills the ledger back in. This is
                        //     how a DAT patched before the ledger existed catches up.
                        || string.Equals(currentDigest, writtenDigest, StringComparison.Ordinal);

                    if (!admitted)
                    {
                        // The English changed under us. Skipping writes nothing: the launcher already
                        // put the current English there, and old Polish would describe the old game
                        // (ADR-0047: English is a worse session, wrong Polish is a broken one).
                        sourceMoved.Add(
                            $"Fragment {translation.GossipId} in file {translation.FileId}: source moved — "
                            + "the DAT no longer holds the English this translation was made for, so it was left untouched");
                        skippedCount++;
                        continue;
                    }

                    fragment.Pieces = [.. pieces];
                    currentSubFileModified = true;
                    pendingLedgerEntries[ledgerKey] = writtenDigest;

                    if (translation.ArgsOrder is not null && fragment.HasArguments)
                    {
                        if (!fragment.TryReorderArgRefs(translation.ArgsOrder))
                        {
                            warnings.Add(
                                $"ArgRefs reorder failed for fragment {translation.GossipId} in file {translation.FileId}");
                        }
                    }

                    appliedCount++;

                    if (appliedCount % ProgressReportInterval == 0)
                    {
                        progress?.Report(new OperationProgress(appliedCount, translations.Count));
                    }
                }
                else
                {
                    warnings.Add($"Fragment {translation.GossipId} not found in file {translation.FileId}");
                    skippedCount++;
                }
            }

            if (currentSubFile is not null && currentFileId != -1)
            {
                WriteCurrentSubFile();
            }

            sourceMoved.Drain(
                warnings,
                count => $"{count} row(s) were left untouched because the DAT no longer holds the English they were "
                    + "translated from (source moved). The game shows its own text for them until the "
                    + "translation is updated.");

            missingSourceDigest.Drain(
                warnings,
                count => $"{count} row(s) carry no source_digest column, so nothing could be verified against the DAT "
                    + "and none of them was written. Re-download the translation file (ADR-0047).");

            // The DAT hits the disk before the ledger and the snapshot say what it holds. If the
            // process died in between, the ledger would describe writes the DAT never got. The finally
            // block flushes on every path that ends earlier, and skips the flush once this one ran.
            _datFileHandler.Flush(datFileHandle);
            datFlushed = true;

            // Read again after the writes. Our patch keeps every iteration (E5, E7), but if a write
            // ever moved one, the old list would report that subfile as replaced on every launch. The
            // read comes before the ledger save: if it threw, `patch` would restore its backup under a
            // ledger that describes the writes it undid. A subfile that failed to load or write above
            // does not stop the save (spec 0012 Tier 0).
            SubFileIterationSnapshot snapshot =
                SubFileIterationSnapshot.FromSubfileSizes(_datFileHandler.GetAllSubfileSizes(datFileHandle));

            if (ledgerChanged)
            {
                Result ledgerSaveResult = _translationLedger.Save(translationsPath, ledgerEntries);

                if (ledgerSaveResult.IsFailure)
                {
                    // The ledger is a hint: a missing entry patches too little rather than hiding a
                    // change. Never fatal, because the DAT is already written.
                    warnings.Add(ledgerSaveResult.Error.Message);
                }
            }

            if (snapshot.Count == 0)
            {
                // A DAT that lists no text subfile after we patched it is a failed read. Saving it would
                // replace a good snapshot with one that can never detect anything.
                warnings.Add(
                    "The DAT listed no text subfile after the patch, so the iteration snapshot was not saved. "
                    + "The next launch compares the DAT with the snapshot of the patch before this one.");
            }
            else
            {
                Result snapshotSaveResult = _snapshotStore.Save(translationsPath, snapshot);

                if (snapshotSaveResult.IsFailure)
                {
                    // Not fatal either: the DAT is already written. The old snapshot stays, so the next
                    // launch may repair once more, and that repair passes the same guard.
                    warnings.Add(snapshotSaveResult.Error.Message);
                }
            }

            PatchSummaryResponse summary = new(
                translations.Count,
                appliedCount,
                skippedCount,
                warnings,
                sourceMoved.Count,
                missingSourceDigest.Count);

            return Result.Success(summary);
        }
        finally
        {
            if (!datFlushed)
            {
                _datFileHandler.Flush(datFileHandle);
            }

            _datFileHandler.Close(datFileHandle);
        }
    }

    /// <summary>
    /// The warnings of one guard category, as a count plus a few examples. Both users of
    /// <see cref="PatchSummaryResponse.Warnings"/> handle the list one entry at a time (<c>patch</c>
    /// prints it, the launch strategy logs it), so the limit belongs here and not in either of them.
    /// </summary>
    private sealed class BoundedGuardWarnings
    {
        private readonly List<string> _samples = [];

        public int Count { get; private set; }

        public void Add(string warning)
        {
            Count++;

            if (_samples.Count < MaxCollectedGuardWarnings)
            {
                _samples.Add(warning);
            }
        }

        public void Drain(List<string> warnings, Func<int, string> describeTotal)
        {
            if (Count == 0)
            {
                return;
            }

            warnings.Add(describeTotal(Count));
            warnings.AddRange(_samples);

            if (Count > _samples.Count)
            {
                warnings.Add($"... and {Count - _samples.Count} more (only the first {MaxCollectedGuardWarnings} are listed).");
            }
        }
    }
}
