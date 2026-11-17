using System.Text.Json;
using System.Text.Json.Serialization;
using LotroKoniecDev.Application.Features.TranslationFileSyncing;
using LotroKoniecDev.Primitives.Constants;
using Microsoft.Extensions.Logging;

namespace LotroKoniecDev.DesktopApp.Presentation.Settings;

/// <summary>
/// The settings the first-run panel and the settings window share. The file is input from outside our
/// trust boundary (ADR-0067 §7), so every value is checked on its own when it is read, and one bad
/// value never wipes the others.
/// </summary>
/// <remarks>
/// Log levels follow the Warning rule (#977): a value only a hand edit or tampering can produce is a
/// Warning; a file the player moved or deleted is Information.
/// </remarks>
public sealed partial class DesktopSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    private readonly ISettingsFile _file;
    private readonly IFileProbe _probe;
    private readonly ILogger<DesktopSettingsStore> _logger;
    private bool _mustSetAsideFile;

    public event EventHandler? Changed;

    public DesktopSettings Current { get; private set; } = DesktopSettings.Default;

    /// <summary>
    /// Reads the file into <see cref="Current"/> and never writes it. A saved game folder is kept even
    /// when its game file is gone: the first-run panel asks before it replaces it.
    /// </summary>
    /// <returns>A Polish note for each value the load dropped.</returns>
    public IReadOnlyList<string> Load()
    {
        SettingsFileDocument? document;
        try
        {
            if (!_file.Exists())
            {
                Current = DesktopSettings.Default;
                return [];
            }

            document = JsonSerializer.Deserialize<SettingsFileDocument>(_file.Read(), SerializerOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogFileUnreadable(_logger, ex);
            return UseDefaultsInsteadOfTheFile(SettingsTexts.FileUnreadable);
        }
        catch (JsonException ex)
        {
            LogFileBroken(_logger, ex);
            return UseDefaultsInsteadOfTheFile(SettingsTexts.FileBroken);
        }

        if (document is null)
        {
            LogFileBroken(_logger, null);
            return UseDefaultsInsteadOfTheFile(SettingsTexts.FileBroken);
        }

        List<string> notes = [];
        Current = new DesktopSettings(
            ReadGameFolder(document.GameFolder, notes),
            ReadTranslationFile(document.TranslationFile, notes),
            document.AutoPatch ?? DesktopSettings.Default.AutoPatchOnLaunch,
            ReadServerAddress(document.ServerAddress, notes));
        return notes;
    }

    /// <summary>
    /// Takes the settings for this run at once and then writes them. The caller has already checked
    /// every value. A file the load could not use is set aside first, so a save never wipes what the
    /// player wrote there.
    /// </summary>
    /// <returns><c>false</c> when the file could not be written; the settings still hold until the app closes.</returns>
    public bool Save(DesktopSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        Current = settings;
        Changed?.Invoke(this, EventArgs.Empty);

        try
        {
            if (_mustSetAsideFile)
            {
                _file.SetAside();
                _mustSetAsideFile = false;
            }

            _file.Write(JsonSerializer.Serialize(SettingsFileDocument.From(settings), SerializerOptions));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogSaveFailed(_logger, ex);
            return false;
        }
    }

    /// <summary>
    /// A game folder is usable only when it holds the game file. A relative path is refused: it would
    /// depend on the folder the app was started from.
    /// </summary>
    public bool ContainsGame(string? folder) =>
        !string.IsNullOrWhiteSpace(folder)
        && Path.IsPathFullyQualified(folder)
        && _probe.FileExists(Path.Combine(folder, DatFileConstants.DatFileName));

    public bool TranslationFileExists(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && Path.IsPathFullyQualified(path)
        && _probe.FileExists(path);

    public DesktopSettingsStore(ISettingsFile file, IFileProbe probe, ILogger<DesktopSettingsStore> logger)
    {
        _file = file;
        _probe = probe;
        _logger = logger;
    }

    private IReadOnlyList<string> UseDefaultsInsteadOfTheFile(string note)
    {
        Current = DesktopSettings.Default;
        _mustSetAsideFile = true;
        return [note];
    }

    private string? ReadGameFolder(string? folder, List<string> notes)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return null;
        }

        if (!Path.IsPathFullyQualified(folder))
        {
            LogValueRefused(_logger, "gameFolder");
            notes.Add(SettingsTexts.GameFolderDropped);
            return null;
        }

        return folder;
    }

    private string? ReadTranslationFile(string? path, List<string> notes)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (!Path.IsPathFullyQualified(path))
        {
            LogValueRefused(_logger, "translationFile");
            notes.Add(SettingsTexts.TranslationFileDropped);
            return null;
        }

        if (!_probe.FileExists(path))
        {
            LogTranslationFileGone(_logger, path);
            notes.Add(SettingsTexts.TranslationFileMissing(path));
            return null;
        }

        return path;
    }

    private string ReadServerAddress(string? address, List<string> notes)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return TmsConstants.DefaultBaseUrl;
        }

        if (!TranslationServerAddress.IsAllowed(address))
        {
            LogValueRefused(_logger, "serverAddress");
            notes.Add(SettingsTexts.ServerAddressDropped);
            return TmsConstants.DefaultBaseUrl;
        }

        return address;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The desktop app could not read its settings file; it uses the defaults")]
    private static partial void LogFileUnreadable(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The desktop app's settings file is not valid JSON of the expected shape; it uses the defaults")]
    private static partial void LogFileBroken(ILogger logger, Exception? exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The desktop app's settings file holds a value the app never writes ({Field}); the value is dropped")]
    private static partial void LogValueRefused(ILogger logger, string field);

    [LoggerMessage(Level = LogLevel.Information, Message = "The translation file the settings name is gone ({Path}); the override is dropped")]
    private static partial void LogTranslationFileGone(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The desktop app could not write its settings file")]
    private static partial void LogSaveFailed(ILogger logger, Exception exception);
}
