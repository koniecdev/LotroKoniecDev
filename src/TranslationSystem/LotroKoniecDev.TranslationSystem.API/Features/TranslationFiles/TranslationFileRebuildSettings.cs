namespace LotroKoniecDev.TranslationSystem.API.Features.TranslationFiles;

/// <summary>
/// The debounce window turns several approves within a few seconds into one rebuild (PERF-04,
/// ADR-0021). It is also how far the artifact can lag a commit, plus the rebuild itself. Zero turns the
/// wait off.
/// </summary>
internal sealed class TranslationFileRebuildSettings
{
    public const string ConfigurationSection = "TranslationFileRebuild";

    /// <summary>
    /// Checked at startup: more than a few minutes of lag breaks the distribution loop, and above about
    /// 49 days <see cref="Task.Delay(TimeSpan)"/> would overflow and stop the host.
    /// </summary>
    public static readonly TimeSpan MaxDebounceWindow = TimeSpan.FromMinutes(5);

    public TimeSpan DebounceWindow { get; init; } = TimeSpan.FromSeconds(2);
}
