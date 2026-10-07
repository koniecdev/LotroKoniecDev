namespace LotroKoniecDev.TranslationSystem.API.Features.TranslationFiles;

/// <summary>
/// Where <see cref="TranslationFileDiskCache"/> keeps its copies (ADR-0064). Only one API process may
/// use a directory, because each process removes the copies it no longer needs. The deployed stacks run
/// one process per container, so the default in the container's temp folder is enough. Test hosts set
/// their own directory.
/// </summary>
internal sealed class TranslationFileDiskCacheSettings
{
    public const string ConfigurationSection = "TranslationFileDiskCache";

    public string Directory { get; init; } = Path.Combine(Path.GetTempPath(), "lotro-translation-files");
}
