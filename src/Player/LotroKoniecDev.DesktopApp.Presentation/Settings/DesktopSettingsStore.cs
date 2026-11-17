using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using LotroKoniecDev.Application.Features.TranslationFileSyncing;
using LotroKoniecDev.Primitives.Constants;
using Microsoft.Extensions.Logging;

namespace LotroKoniecDev.DesktopApp.Presentation.Settings;

/// <summary>
/// The settings the first-run panel and the settings window share. The file is input from outside our
/// trust boundary (ADR-0067 §7), so every value is checked on its own when it is read, its JSON kind
/// included, and one bad value never wipes the others. Only a file that cannot be read, is not a JSON
/// object, or has an escape that is not text in a value the app reads gives the defaults.
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
        WriteIndented = true,
        // The player may edit the file by hand, so Polish letters in a path stay readable.
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    private static readonly string GameFolderField = FieldName(nameof(SettingsFileDocument.GameFolder));
    private static readonly string TranslationFileField = FieldName(nameof(SettingsFileDocument.TranslationFile));
    private static readonly string AutoPatchField = FieldName(nameof(SettingsFileDocument.AutoPatch));
    private static readonly string ServerAddressField = FieldName(nameof(SettingsFileDocument.ServerAddress));

    private readonly ISettingsFile _file;
    private readonly IFileProbe _probe;
    private readonly ILogger<DesktopSettingsStore> _logger;
    private bool _mustSetAsideFile;

    /// <summary>Raised after every save, when the settings in use have changed, whether the write worked or not.</summary>
    public event EventHandler<SettingsSavedEventArgs>? Saved;

    public DesktopSettings Current { get; private set; } = DesktopSettings.Default;

    /// <summary>
    /// Reads the file into <see cref="Current"/> and never writes it. A saved game folder is kept even
    /// when its game file is gone: the first-run panel asks before it replaces it.
    /// </summary>
    /// <returns>A Polish note for each value the load dropped.</returns>
    public IReadOnlyList<string> Load()
    {
        _mustSetAsideFile = false;
        JsonDocument document;
        try
        {
            if (!_file.Exists())
            {
                Current = DesktopSettings.Default;
                return [];
            }

            document = JsonDocument.Parse(_file.Read());
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

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                LogFileBroken(_logger, null);
                return UseDefaultsInsteadOfTheFile(SettingsTexts.FileBroken);
            }

            List<string> notes = [];
            try
            {
                Current = new DesktopSettings(
                    ReadGameFolder(ReadString(root, GameFolderField, SettingsTexts.GameFolderDropped, notes), notes),
                    ReadTranslationFile(ReadString(root, TranslationFileField, SettingsTexts.TranslationFileDropped, notes), notes),
                    ReadAutoPatch(root, notes),
                    ReadServerAddress(ReadString(root, ServerAddressField, SettingsTexts.ServerAddressDropped, notes), notes));
            }
            catch (InvalidOperationException ex)
            {
                // JsonDocument accepts an escape that is not text (a lone "\uD800"); reading a value or
                // matching a field name with one throws only here.
                LogFileBroken(_logger, ex);
                return UseDefaultsInsteadOfTheFile(SettingsTexts.FileBroken);
            }

            return notes;
        }
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
        bool fileSetAside = false;
        bool written;
        try
        {
            if (_mustSetAsideFile)
            {
                _file.SetAside();
                _mustSetAsideFile = false;
                fileSetAside = true;
            }

            _file.Write(JsonSerializer.Serialize(SettingsFileDocument.From(settings), SerializerOptions));
            written = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogSaveFailed(_logger, ex);
            written = false;
        }

        Saved?.Invoke(this, new SettingsSavedEventArgs(written, fileSetAside));
        return written;
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

    private static string FieldName(string propertyName) => JsonNamingPolicy.CamelCase.ConvertName(propertyName);

    /// <summary>A missing field and a JSON <c>null</c> mean "not set". Any other kind than a string is dropped with its note.</summary>
    private string? ReadString(JsonElement root, string field, string droppedNote, List<string> notes)
    {
        if (!root.TryGetProperty(field, out JsonElement value))
        {
            return null;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                return value.GetString();
            case JsonValueKind.Null:
                return null;
            default:
                LogValueRefused(_logger, field);
                notes.Add(droppedNote);
                return null;
        }
    }

    private bool ReadAutoPatch(JsonElement root, List<string> notes)
    {
        if (!root.TryGetProperty(AutoPatchField, out JsonElement value))
        {
            return DesktopSettings.Default.AutoPatchOnLaunch;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Null:
                return DesktopSettings.Default.AutoPatchOnLaunch;
            default:
                LogValueRefused(_logger, AutoPatchField);
                notes.Add(SettingsTexts.AutoPatchDropped);
                return DesktopSettings.Default.AutoPatchOnLaunch;
        }
    }

    private string? ReadGameFolder(string? folder, List<string> notes)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return null;
        }

        if (!Path.IsPathFullyQualified(folder))
        {
            LogValueRefused(_logger, GameFolderField);
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
            LogValueRefused(_logger, TranslationFileField);
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
            LogValueRefused(_logger, ServerAddressField);
            notes.Add(SettingsTexts.ServerAddressDropped);
            return TmsConstants.DefaultBaseUrl;
        }

        return address;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The desktop app could not read its settings file; it uses the defaults")]
    private static partial void LogFileUnreadable(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The desktop app's settings file is not a JSON object of valid text; it uses the defaults")]
    private static partial void LogFileBroken(ILogger logger, Exception? exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The desktop app's settings file holds a value the app never writes ({Field}); the value is dropped")]
    private static partial void LogValueRefused(ILogger logger, string field);

    [LoggerMessage(Level = LogLevel.Information, Message = "The translation file the settings name is gone ({Path}); the override is dropped")]
    private static partial void LogTranslationFileGone(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The desktop app could not write its settings file")]
    private static partial void LogSaveFailed(ILogger logger, Exception exception);
}
