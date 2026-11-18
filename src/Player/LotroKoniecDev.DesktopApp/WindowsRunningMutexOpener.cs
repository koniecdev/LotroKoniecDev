using LotroKoniecDev.DesktopApp.Presentation;

namespace LotroKoniecDev.DesktopApp;

/// <summary>
/// The app never takes ownership of the mutex: its handle alone keeps the name alive, and that is all
/// the next copy and the uninstaller look for. A mutex Windows will not let the app open still
/// exists, most likely held by a copy in another account, so it counts as one.
/// </summary>
internal sealed class WindowsRunningMutexOpener : IRunningMutexOpener
{
    public RunningMutexAttempt Open(string name)
    {
        try
        {
            Mutex mutex = new(initiallyOwned: false, name, out bool createdNew);
            return new RunningMutexAttempt(
                createdNew ? RunningMutexState.Created : RunningMutexState.AlreadyExists,
                mutex);
        }
        catch (UnauthorizedAccessException)
        {
            return new RunningMutexAttempt(RunningMutexState.AlreadyExists, Handle: null);
        }
        catch (Exception ex) when (ex is IOException or WaitHandleCannotBeOpenedException)
        {
            return new RunningMutexAttempt(RunningMutexState.Refused, Handle: null);
        }
    }
}
