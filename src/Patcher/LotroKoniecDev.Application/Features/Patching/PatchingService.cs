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
internal sealed class PatchingService : IPatchingService
{
    private const int ProgressReportInterval = 1000;

    /// <summary>
    /// The same limit the parser uses. <c>source moved</c> can hit thousands of rows after a big
    /// update (U49 changed 1,644 sources), and <c>no source digest</c> hits every row of a six-column
    /// file. So both are reported as a count plus a few examples instead of one line each.
    /// The counting happens here because both users of the summary meet at this point: <c>patch</c>
    /// prints the list, and the launch strategy logs it line by line.
    /// </summary>
    private const int MaxCollectedGuardWarnings = 100;

    private readonly IDatFileHandler _datFileHandler;
    private readonly ITranslationParser _translationParser;
    private readonly ITranslationLedger _translationLedger;

    public PatchingService(
        IDatFileHandler datFileHandler,
        ITranslationParser translationParser,
        ITranslationLedger translationLedger)
    {
        _datFileHandler = datFileHandler;
        _translationParser = translationParser;
        _translationLedger = translationLedger;
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

        // A line the parser rejected is always reported (ADR-0042). Its warning travels with the
        // per-fragment ones into the summary the CLI prints.
        IReadOnlyList<string> parseWarnings = translationParseResult.Value.Warnings;

        if (translations.Count == 0)
        {
            // When every line was rejected, the user has to be told why. The warning list is printed
            // only after a successful patch, so on this path the reason has to travel inside the error
            // itself. Otherwise ADR-0042 would fail exactly where it matters most.
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

        // The finally block reads this, so it has to be declared outside the try.
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

            // What this patcher wrote before, and what it is writing now (ADR-0047 §4). The set read
            // from disk is added to and updated, never rebuilt: an entry for a row that left the
            // artifact still describes what sits on that fragment, and dropping it would leave the
            // fragment stuck on our older Polish forever.
            Dictionary<LedgerKey, string> ledgerEntries = new(_translationLedger.Read(translationsPath));
            Dictionary<LedgerKey, string> pendingLedgerEntries = [];
            bool ledgerChanged = false;

            BoundedGuardWarnings sourceMoved = new();
            BoundedGuardWarnings missingSourceDigest = new();

            // A row reaches the DAT only when its whole subfile is written back, so its ledger entry
            // waits until then and is dropped when the write fails (ADR-0047 §4).
            void WriteCurrentSubFile()
            {
                // When the guard refused every row of a subfile, that subfile still holds exactly what
                // the launcher put there, so writing it back would change the game's archive for
                // nothing. On update day that is most of them, and "skipped" has to mean "wrote
                // nothing".
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

                // Checked here, before a subfile is loaded and long before one is changed, because
                // this is the last point where a row that cannot be written is still only a row (#598,
                // ADR-0043). The only way to get one is a hand-edited or hostile file: the TMS already
                // limits the text at the API.
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

                // Checked before a subfile is loaded, like the piece-length guard above. A translation
                // file that is six columns throughout cannot be patched at all (ADR-0047 §3), and
                // deciding that here saves a subfile load for each of about 800k rows. We need nothing
                // from the fragment to see that the row carries no digest.
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
                        // (a) the English this translation was made for, either untouched or put back
                        //     by the launcher, which is what the repair paths are for.
                        SourceDigest.Matches(currentDigest, translation.SourceDigest)
                        // (b) what this patcher last wrote there, so a newer translation of the same
                        //     English can still land on a fragment that already holds Polish.
                        //     Entries written into the subfile currently in memory count too. A
                        //     hand-made file can list the same key twice, and the last one wins, so the
                        //     second row must see the first row's write and not the disk.
                        || (pendingLedgerEntries.TryGetValue(ledgerKey, out string? recordedDigest)
                                || ledgerEntries.TryGetValue(ledgerKey, out recordedDigest))
                            && SourceDigest.Matches(currentDigest, recordedDigest)
                        // (c) exactly what this row would write. The write then changes nothing, only
                        //     our own patch could have put that text there, and it fills the ledger
                        //     back in. This is how a DAT patched before the ledger existed catches up.
                        || string.Equals(currentDigest, writtenDigest, StringComparison.Ordinal);

                    if (!admitted)
                    {
                        // The English changed under us. Skipping writes nothing: the launcher already
                        // put the current English there, and old Polish would describe the old game.
                        // ADR-0047 puts it this way: English is a worse session, wrong Polish is a
                        // broken one.
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

            if (ledgerChanged)
            {
                // The DAT hits the disk before the ledger says what it holds. If the process were
                // killed between the two, the ledger would describe writes the DAT never got, and
                // those rows would match nothing on the next run. The finally block flushes on every
                // other path, so it skips this one once it has run.
                _datFileHandler.Flush(datFileHandle);
                datFlushed = true;

                Result ledgerSaveResult = _translationLedger.Save(translationsPath, ledgerEntries);

                if (ledgerSaveResult.IsFailure)
                {
                    // The ledger is a hint, not the truth. The next run reads it again, and a missing
                    // entry patches too little rather than hiding a change. This is never fatal: the
                    // DAT is already written.
                    warnings.Add(ledgerSaveResult.Error.Message);
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
    /// The warnings of one guard category, as a count plus a few examples. Either category can cover
    /// the whole corpus, and both users of <see cref="PatchSummaryResponse.Warnings"/> print or log
    /// the list one entry at a time, so the limit belongs here and not in either of them.
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
