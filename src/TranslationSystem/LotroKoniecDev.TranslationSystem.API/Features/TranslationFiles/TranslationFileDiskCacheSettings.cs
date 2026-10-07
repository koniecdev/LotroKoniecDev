namespace LotroKoniecDev.TranslationSystem.API.Features.TranslationFiles;

/// <summary>
/// Where <see cref="TranslationFileDiskCache"/> keeps its copies (ADR-0064). When the setting is left
/// out, each API process makes its own private folder in the temp folder; an empty value is refused at
/// startup. A configured folder is used as it is, so it must belong to one API process alone: each
/// process removes the copies it does not need, and any file placed there with the right name is
/// served. The containers set one, so a restart reuses its copy, and test hosts set one so they can
/// look inside it.
/// </summary>
internal sealed class TranslationFileDiskCacheSettings
{
    public const string ConfigurationSection = "TranslationFileDiskCache";

    public string? Directory { get; init; }
}
