namespace LotroKoniecDev.TranslationSystem.API.Features.TranslationFiles;

/// <summary>
/// Where <see cref="TranslationFileDiskCache"/> keeps its copies (ADR-0064). Left empty, which is the
/// deployed setup, each API process makes its own private folder in the temp folder. A configured
/// folder is used as it is, so it must belong to one API process alone: each process removes the
/// copies it does not need, and any file placed there with the right name is served. Test hosts set
/// one so they can look inside it.
/// </summary>
internal sealed class TranslationFileDiskCacheSettings
{
    public const string ConfigurationSection = "TranslationFileDiskCache";

    public string? Directory { get; init; }
}
