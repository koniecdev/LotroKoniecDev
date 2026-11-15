namespace LotroKoniecDev.Tests.E2E;

/// <summary>
/// One copy of the DAT, patched once with the repo's translation file, for every test of one class
/// that checks a fact about that patch (#1408). The facts are read right around the one patch, so a
/// test that patches the copy again later changes nothing another test checks. The copy is made at
/// its first use and deleted when the class's last test is done.
/// </summary>
public sealed class PatchedDatCopy : IDisposable
{
    private readonly E2ETestFixture _fixture;
    private string? _tempDir;
    private Task<PatchedDat>? _patchedDat;

    public Task<PatchedDat> GetAsync() => _patchedDat ??= PatchAsync();

    public void Dispose()
    {
        if (_tempDir is not null)
        {
            _fixture.DeleteTempDir(_tempDir);
        }
    }

    public PatchedDatCopy(E2ETestFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<PatchedDat> PatchAsync()
    {
        string datPath = _fixture.CreateTempDatCopy();
        _tempDir = Path.GetDirectoryName(datPath)!;
        long sizeBeforePatch = new FileInfo(datPath).Length;
        DateTime lastWriteBeforePatchUtc = File.GetLastWriteTimeUtc(datPath);

        CliResult patch = await _fixture.RunCliAsync(
            $"patch \"{_fixture.TranslationsPolishPath}\" -d \"{datPath}\"");
        DateTime lastWriteAfterPatchUtc = File.GetLastWriteTimeUtc(datPath);
        FileInfo backup = new(PatchedDat.BackupPathOf(datPath));
        long? backupSizeAfterPatch = backup.Exists ? backup.Length : null;

        string exportAfterPatchPath = Path.Combine(_tempDir, "after.txt");
        CliResult exportAfterPatch = await _fixture.RunCliAsync(
            $"export -d \"{datPath}\" -o \"{exportAfterPatchPath}\"");

        return new PatchedDat(
            datPath,
            sizeBeforePatch,
            lastWriteBeforePatchUtc,
            lastWriteAfterPatchUtc,
            backupSizeAfterPatch,
            patch,
            exportAfterPatchPath,
            exportAfterPatch);
    }
}

/// <param name="BackupSizeAfterPatch">The backup's size right after the one patch; <c>null</c> when
/// the patch made no backup.</param>
public sealed record PatchedDat(
    string DatPath,
    long SizeBeforePatch,
    DateTime LastWriteBeforePatchUtc,
    DateTime LastWriteAfterPatchUtc,
    long? BackupSizeAfterPatch,
    CliResult Patch,
    string ExportAfterPatchPath,
    CliResult ExportAfterPatch)
{
    public string BackupPath => BackupPathOf(DatPath);

    public static string BackupPathOf(string datPath) => datPath + ".backup";
}
