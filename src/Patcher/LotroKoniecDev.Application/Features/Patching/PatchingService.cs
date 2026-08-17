using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Abstractions.DatFilesServices;
using LotroKoniecDev.Application.Extensions;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Models;
using LotroKoniecDev.Primitives.Constants;
using LotroKoniecDev.Primitives.Enums;

namespace LotroKoniecDev.Application.Features.Patching;

/// <summary>
/// Writes approved translations into the DAT, one fragment at a time, subject to the per-row source
/// guard of ADR-0047: stale Polish never lands over changed English, on any write path.
/// </summary>
internal sealed class PatchingService : IPatchingService
{
    private const int ProgressReportInterval = 1000;

    /// <summary>
    /// Mirrors the parser's own cap: <c>source moved</c> can hit thousands of rows after a major
    /// update (U49 changed 1,644 sources) and <c>no source digest</c> hits every row of a
    /// six-column file, so both are reported as a count plus a bounded sample rather than streamed.
    /// The aggregation lives here because both consumers — <c>patch</c>, which prints the list, and
    /// the launch strategy, which logs it line by line — meet at this summary.
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

        // A line the parser rejected is reported, never swallowed (ADR-0042): its warning rides
        // along with the per-fragment ones into the summary the CLI prints.
        IReadOnlyList<string> parseWarnings = translationParseResult.Value.Warnings;

        if (translations.Count == 0)
        {
            // A file whose every line was rejected must say WHY. The warning list is only printed on
            // a successful patch, so on this path the diagnosis has to ride in the error itself —
            // otherwise the fix of ADR-0042 would stop exactly where it matters most.
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

        // Read by the finally block, so it lives outside the try.
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

            // What this patcher has written before, and what it is writing now (ADR-0047 §4). The
            // set read from disk is UPSERTED, never rebuilt: an entry for a row that left the
            // artifact still describes what sits on that fragment, and dropping it would strand the
            // fragment on our older Polish forever.
            Dictionary<LedgerKey, string> ledgerEntries = new(_translationLedger.Read(translationsPath));
            Dictionary<LedgerKey, string> pendingLedgerEntries = [];
            bool ledgerChanged = false;

            BoundedGuardWarnings sourceMoved = new();
            BoundedGuardWarnings missingSourceDigest = new();

            // A row only reaches the DAT when its whole subfile is written back, so its ledger entry
            // is held until then and dropped when the write fails (ADR-0047 §4).
            void WriteCurrentSubFile()
            {
                // A subfile every row of which the guard refused holds exactly what the launcher put
                // there, so writing it back would be a pointless mutation of the game's archive —
                // and on update day that is most of them. "Skipped" has to mean "wrote nothing".
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

                // Screened here — before a subfile is loaded, and well before one is mutated — because
                // this is the last point at which an unwritable row is still just a row (#598, ADR-0043).
                // A hand-edited or hostile file is the only source: the TMS caps the text at the API.
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

                // Screened before a subfile is loaded, like the piece-length guard above: a wholly
                // six-column translation file is unpatchable in its entirety (ADR-0047 §3), and
                // deciding that here spares ~800k rows a subfile load each. Nothing about the
                // fragment is needed to know the row carries no digest to check it against.
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

                    // What the fragment holds right now, and what it would hold once written — both
                    // in the export form the digest is defined over (ADR-0047 §3).
                    string currentDigest = SourceDigest.ForFragment(fragment);
                    string writtenDigest = SourceDigest.ForExportForm(translation.Content, fragment.ArgRefs.Count);

                    bool admitted =
                        // (a) the English this translation was made for — pristine, or collaterally
                        //     reverted by the launcher, which is what the repair paths exist for.
                        SourceDigest.Matches(currentDigest, translation.SourceDigest)
                        // (b) what this patcher last wrote there, so a NEWER translation for the same
                        //     English can still land on a fragment that already holds Polish.
                        //     The entries written into the subfile currently in memory count too:
                        //     a file listing the same key twice (hand-made) used to be last-wins,
                        //     and the second row must see the first one's write, not the disk.
                        || (pendingLedgerEntries.TryGetValue(ledgerKey, out string? recordedDigest)
                                || ledgerEntries.TryGetValue(ledgerKey, out recordedDigest))
                            && SourceDigest.Matches(currentDigest, recordedDigest)
                        // (c) exactly what this row would write — the write is a no-op, nothing but
                        //     our own patch puts that text there, and it re-seeds the ledger. This
                        //     is what bootstraps a DAT patched before the ledger existed.
                        || string.Equals(currentDigest, writtenDigest, StringComparison.Ordinal);

                    if (!admitted)
                    {
                        // The English moved under us. Skipping writes nothing — the launcher already
                        // put the current English there, and stale Polish would describe the old
                        // game (ADR-0047: English is a degraded session, stale Polish a broken one).
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
                // The DAT reaches disk before the ledger claims what it holds: a process killed between
                // the two would otherwise leave a ledger describing writes the DAT never received, and
                // those rows would then match nothing on the next run. The finally block is what
                // guarantees a flush on every OTHER path, so it is skipped once this one ran.
                _datFileHandler.Flush(datFileHandle);
                datFlushed = true;

                Result ledgerSaveResult = _translationLedger.Save(translationsPath, ledgerEntries);

                if (ledgerSaveResult.IsFailure)
                {
                    // A hint, not the truth: the next run re-reads it, and an absent entry
                    // under-patches rather than masking. Never fatal — the DAT is already written.
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
    /// One guard category's warnings as a count plus a bounded sample. Both categories can cover a
    /// whole corpus, and the two consumers of <see cref="PatchSummaryResponse.Warnings"/> print or
    /// log the list one entry at a time, so the bound has to live here rather than in either of them.
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
