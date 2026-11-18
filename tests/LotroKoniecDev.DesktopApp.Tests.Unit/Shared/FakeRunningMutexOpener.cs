using LotroKoniecDev.DesktopApp.Presentation;

namespace LotroKoniecDev.DesktopApp.Tests.Unit.Shared;

/// <summary>Answers each mutex name with the state a test sets, and keeps every handle it gave out.</summary>
internal sealed class FakeRunningMutexOpener : IRunningMutexOpener
{
    private readonly RunningMutexState _global;
    private readonly RunningMutexState _local;
    private readonly List<FakeHandle> _handles = [];

    public IReadOnlyList<FakeHandle> Handles => _handles;

    public RunningMutexAttempt Open(string name)
    {
        RunningMutexState state = name switch
        {
            RunningCopyClaim.GlobalMutexName => _global,
            RunningCopyClaim.MutexName => _local,
            _ => throw new ArgumentException($"Unexpected mutex name {name}.", nameof(name))
        };

        if (state is RunningMutexState.Refused)
        {
            return new RunningMutexAttempt(state, Handle: null);
        }

        FakeHandle handle = new();
        _handles.Add(handle);
        return new RunningMutexAttempt(state, handle);
    }

    public FakeRunningMutexOpener(RunningMutexState global, RunningMutexState local)
    {
        _global = global;
        _local = local;
    }

    internal sealed class FakeHandle : IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Dispose() => IsDisposed = true;
    }
}
