namespace LotroKoniecDev.DesktopApp.Presentation;

/// <summary>Opens a named mutex. The exe gives the Windows one, so the single-copy rule is tested on any OS.</summary>
public interface IRunningMutexOpener
{
    RunningMutexAttempt Open(string name);
}
