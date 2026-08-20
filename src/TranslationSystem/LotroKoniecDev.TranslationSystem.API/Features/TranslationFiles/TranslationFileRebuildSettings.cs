namespace LotroKoniecDev.TranslationSystem.API.Features.TranslationFiles;

/// <summary>
/// The setting for the background artifact rebuild (PERF-04, ADR-0021). The worker waits this long
/// after the first signal before it rebuilds, so several approves within a few seconds cause one
/// rebuild over the whole catalog instead of one per approve.
/// It is also the longest the artifact can lag behind a committed write, plus the rebuild itself. It is
/// short in Testing so integration tests finish quickly, and zero is allowed and turns the wait off.
/// </summary>
internal sealed class TranslationFileRebuildSettings
{
    public const string ConfigurationSection = "TranslationFileRebuild";

    /// <summary>
    /// The largest value allowed, checked at startup. The wait is also how far behind the artifact can
    /// fall, so anything beyond a few minutes breaks the distribution loop. A very large value, above
    /// about 49 days, would also overflow <see cref="Task.Delay(TimeSpan)"/> in the worker and stop the
    /// host.
    /// </summary>
    public static readonly TimeSpan MaxDebounceWindow = TimeSpan.FromMinutes(5);

    public TimeSpan DebounceWindow { get; init; } = TimeSpan.FromSeconds(2);
}
