namespace LotroKoniecDev.TranslationSystem.API.Features.TranslationFiles;

/// <summary>
/// Left out, each process makes a private temp folder; an empty value is refused at startup. A
/// configured folder must belong to one API process alone: each process removes copies it does not
/// need, and any file there with the right name is served (ADR-0064). The containers set one so a
/// restart reuses its copy.
/// </summary>
internal sealed class TranslationFileDiskCacheSettings
{
    public const string ConfigurationSection = "TranslationFileDiskCache";

    public string? Directory { get; init; }
}
