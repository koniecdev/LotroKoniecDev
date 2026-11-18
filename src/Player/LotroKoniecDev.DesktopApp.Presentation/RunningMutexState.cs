namespace LotroKoniecDev.DesktopApp.Presentation;

/// <summary>What Windows answered when the app asked for one of its named mutexes.</summary>
public enum RunningMutexState
{
    Created,
    AlreadyExists,

    /// <summary>Windows gave no handle, for example because another kind of object holds the name.</summary>
    Refused
}
