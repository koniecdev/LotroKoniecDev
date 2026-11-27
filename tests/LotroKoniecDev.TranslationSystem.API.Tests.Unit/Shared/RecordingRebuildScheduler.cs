using LotroKoniecDev.TranslationSystem.API.Features.TranslationFiles;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Shared;

/// <summary>
/// The scheduler is internal, which NSubstitute cannot proxy. It writes into a shared call list, so a test
/// can check that the rebuild comes after the commit.
/// </summary>
internal sealed class RecordingRebuildScheduler : ITranslationFileRebuildScheduler
{
    private readonly List<string> _callOrder;

    public int ScheduleCount { get; private set; }

    public void Schedule(string language)
    {
        ScheduleCount++;
        _callOrder.Add(nameof(ITranslationFileRebuildScheduler.Schedule));
    }

    public RecordingRebuildScheduler(List<string> callOrder)
    {
        _callOrder = callOrder;
    }
}
