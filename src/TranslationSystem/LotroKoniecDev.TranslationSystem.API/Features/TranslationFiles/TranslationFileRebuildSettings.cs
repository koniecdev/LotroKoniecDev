namespace LotroKoniecDev.TranslationSystem.API.Features.TranslationFiles;

/// <summary>
/// Tuning knob for the debounced background artifact rebuild (PERF-04, ADR-0021). The worker waits
/// this long after the first dirty signal before rebuilding, so a reviewer burst (k approves in a
/// few seconds) collapses into one O(N) rebuild instead of k serialized ones. It is also the upper
/// bound the artifact can lag a committed write by (plus one rebuild). Short in Testing so
/// integration tests converge fast; zero is valid and disables the coalescing wait entirely.
/// </summary>
internal sealed class TranslationFileRebuildSettings
{
    public const string ConfigurationSection = "TranslationFileRebuild";

    /// <summary>
    /// Upper bound enforced at startup. The window is also the artifact's staleness bound, so
    /// anything beyond minutes defeats the distribution loop — and an absurd value (~&gt; 49 days)
    /// would overflow <see cref="Task.Delay(TimeSpan)"/> inside the worker and stop the host.
    /// </summary>
    public static readonly TimeSpan MaxDebounceWindow = TimeSpan.FromMinutes(5);

    public TimeSpan DebounceWindow { get; init; } = TimeSpan.FromSeconds(2);
}
