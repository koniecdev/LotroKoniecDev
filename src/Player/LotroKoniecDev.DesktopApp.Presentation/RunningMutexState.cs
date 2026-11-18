namespace LotroKoniecDev.DesktopApp.Presentation;

/// <summary>What Windows answered when the app asked for one of its named mutexes.</summary>
public enum RunningMutexState
{
    Created,

    /// <summary>Another holder has the mutex, also when Windows will not let the app open it.</summary>
    AlreadyExists,

    /// <summary>Windows gave no handle, for example because another kind of object holds the name.</summary>
    Refused
}
