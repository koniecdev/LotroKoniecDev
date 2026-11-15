namespace LotroKoniecDev.Tests.E2E;

/// <summary>
/// One copy of the DAT, patched once with the repo's translation file before any test checks it, for
/// every test of one class that checks a fact about that patch (#1408). The facts are read right
/// around the one patch, so a test that patches the copy again later changes nothing another test
/// checks. The copy is made at its first use and deleted when the class's last test is done.
/// </summary>
public sealed class PatchedDatCopy : IDisposable
{
    private readonly E2ETestFixture _fixture;
    private readonly SharedDatCopy _copy;
    private Task<PatchedDat>? _patchedDat;

    public Task<PatchedDat> GetAsync() => _patchedDat ??= PatchAsync();

    public void Dispose() => _copy.Dispose();

    public PatchedDatCopy(E2ETestFixture fixture)
    {
        _fixture = fixture;
        _copy = new SharedDatCopy(fixture);
    }

    private async Task<PatchedDat> PatchAsync()
    {
        string datPath = _copy.GetDatPath();
        long sizeBeforePatch = new FileInfo(datPath).Length;
        DateTime lastWriteBeforePatchUtc = File.GetLastWriteTimeUtc(datPath);

        CliResult patch = await _fixture.RunCliAsync(
            $"patch \"{_fixture.TranslationsPolishPath}\" -d \"{datPath}\"");
        DateTime lastWriteAfterPatchUtc = File.GetLastWriteTimeUtc(datPath);
        FileInfo backup = new(PatchedDat.BackupPathOf(datPath));
        long? backupSizeAfterPatch = backup.Exists ? backup.Length : null;
        DateTime? backupLastWriteAfterPatchUtc = backup.Exists ? backup.LastWriteTimeUtc : null;

        // A failed export is kept, not thrown: only the round-trip tests read it, and the other tests
        // must still report their own result.
        string exportAfterPatchPath = Path.Combine(Path.GetDirectoryName(datPath)!, "after.txt");
        CliResult? exportAfterPatch = null;
        Exception? exportAfterPatchError = null;

        try
        {
            exportAfterPatch = await _fixture.RunCliAsync(
                $"export -d \"{datPath}\" -o \"{exportAfterPatchPath}\"");
        }
        catch (Exception exception)
        {
            exportAfterPatchError = exception;
        }

        return new PatchedDat(
            datPath,
            sizeBeforePatch,
            lastWriteBeforePatchUtc,
            lastWriteAfterPatchUtc,
            backupSizeAfterPatch,
            backupLastWriteAfterPatchUtc,
            patch,
            exportAfterPatchPath,
            exportAfterPatch,
            exportAfterPatchError);
    }
}

/// <param name="BackupSizeAfterPatch">The backup's size right after the one patch; <c>null</c> when
/// the patch made no backup.</param>
/// <param name="ExportAfterPatchError">Why the export after the patch did not finish; <c>null</c>
/// when it did, and then <paramref name="ExportAfterPatch"/> holds its result.</param>
public sealed record PatchedDat(
    string DatPath,
    long SizeBeforePatch,
    DateTime LastWriteBeforePatchUtc,
    DateTime LastWriteAfterPatchUtc,
    long? BackupSizeAfterPatch,
    DateTime? BackupLastWriteAfterPatchUtc,
    CliResult Patch,
    string ExportAfterPatchPath,
    CliResult? ExportAfterPatch,
    Exception? ExportAfterPatchError)
{
    public string BackupPath => BackupPathOf(DatPath);

    public static string BackupPathOf(string datPath) => datPath + ".backup";
}
