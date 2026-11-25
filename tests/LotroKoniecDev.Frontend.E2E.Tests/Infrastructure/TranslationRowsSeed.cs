using System.Globalization;
using System.Text;
using Microsoft.Playwright;

namespace LotroKoniecDev.Frontend.E2E.Tests.Infrastructure;

/// <summary>
/// The translation rows of every flow that needs them, seeded once per stack through the site as the admin
/// (ADR-0009 §5 amendment). An import removes every row it does not carry, so the flows share one import,
/// and each flow searches for its own token. The flows of one collection run one at a time, so the seed
/// needs no lock.
/// </summary>
internal sealed class TranslationRowsSeed
{
    internal const int WayBackFileId = 620_756_992;
    internal const int InlineSaveFileId = 620_756_993;

    /// <summary>
    /// Two pages of 25 per flow, and the second page still has rows after a save moves one row out of a
    /// status filter.
    /// </summary>
    internal const int RowsPerFlow = 30;

    /// <summary>The one inline-save row whose English has a placeholder, so a Polish text without it is refused.</summary>
    internal const int InlineSavePlaceholderGossipId = 22;

    private const string GameVersion = "48";

    private static readonly LocatorWaitForOptions LongWait = new() { Timeout = 30_000 };

    private bool _versionRegistered;
    private bool _seeded;

    public string WayBackToken { get; } = NewToken("waybk");

    public string InlineSaveToken { get; } = NewToken("inlsv");

    /// <summary>The page must already be signed in as the admin.</summary>
    public async Task EnsureSeededAsync(IPage page, string frontendBaseUrl)
    {
        if (_seeded)
        {
            return;
        }

        // The version stays registered when the import fails, and a second registration would never succeed.
        if (!_versionRegistered)
        {
            await RegisterGameVersionAsync(page, frontendBaseUrl);
            _versionRegistered = true;
        }

        await ImportRowsAsync(page, frontendBaseUrl);
        _seeded = true;
    }

    private static string NewToken(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..13];

    private static async Task RegisterGameVersionAsync(IPage page, string frontendBaseUrl)
    {
        await page.GotoAsync($"{frontendBaseUrl}/game-versions");
        await page.GetByLabel("Wersja", new() { Exact = true }).FillAsync(GameVersion);
        await page.GetByRole(AriaRole.Button, new() { Name = "Zarejestruj", Exact = true }).ClickAsync();
        await page.GetByText($"Zarejestrowano wersję {GameVersion}.", new() { Exact = true }).WaitForAsync(LongWait);
    }

    private async Task ImportRowsAsync(IPage page, string frontendBaseUrl)
    {
        IEnumerable<string> wayBackLines = Enumerable.Range(1, RowsPerFlow).Select(gossipId =>
            string.Create(CultureInfo.InvariantCulture, $"{WayBackFileId}||{gossipId}||Row {gossipId:00} of {WayBackToken}||NULL||NULL||1"));
        IEnumerable<string> inlineSaveLines = Enumerable.Range(1, RowsPerFlow).Select(gossipId =>
            gossipId == InlineSavePlaceholderGossipId
                ? string.Create(CultureInfo.InvariantCulture, $"{InlineSaveFileId}||{gossipId}||Row {gossipId:00} of {InlineSaveToken} keeps <--DO_NOT_TOUCH!--> here||1||1||1")
                : string.Create(CultureInfo.InvariantCulture, $"{InlineSaveFileId}||{gossipId}||Row {gossipId:00} of {InlineSaveToken}||NULL||NULL||1"));
        string exported = string.Join('\n', wayBackLines.Concat(inlineSaveLines));

        await page.GotoAsync($"{frontendBaseUrl}/import-export");
        await page.GetByLabel("Plik exported.txt", new() { Exact = true }).SetInputFilesAsync(new FilePayload
        {
            Name = "exported.txt",
            MimeType = "text/plain",
            Buffer = Encoding.UTF8.GetBytes(exported)
        });
        await page.GetByRole(AriaRole.Button, new() { Name = "Importuj", Exact = true }).ClickAsync();
        await page.GetByText("Import zakończony powodzeniem.", new() { Exact = true }).WaitForAsync(LongWait);
    }
}
