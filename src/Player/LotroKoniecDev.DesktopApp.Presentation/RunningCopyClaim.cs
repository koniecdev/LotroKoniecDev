namespace LotroKoniecDev.DesktopApp.Presentation;

/// <summary>
/// One running copy of the app on the PC (#1464). The <c>Global\</c> mutex decides, so a copy in
/// another Windows account counts too: every account shares one install, one settings file and one game.
/// The plain name decides only when Windows refuses the global one. The uninstaller checks both names
/// (installer/LotroPoPolsku.iss).
/// </summary>
public sealed class RunningCopyClaim : IDisposable
{
    public const string MutexName = "LotroPoPolsku_Running";
    public const string GlobalMutexName = @"Global\" + MutexName;

    private readonly IDisposable? _globalHandle;
    private readonly IDisposable? _localHandle;

    public bool IsRefused { get; }

    public bool GlobalHeld => _globalHandle is not null;

    public bool LocalHeld => _localHandle is not null;

    public void Dispose()
    {
        _globalHandle?.Dispose();
        _localHandle?.Dispose();
    }

    /// <summary>
    /// A refused claim has already closed both handles, so a second copy that waits on its message
    /// never keeps the name alive after the first copy ends. A claim Windows refused on both names
    /// starts anyway: a name another program holds must not lock the player out.
    /// </summary>
    public static RunningCopyClaim Create(IRunningMutexOpener opener)
    {
        ArgumentNullException.ThrowIfNull(opener);

        RunningMutexAttempt global = opener.Open(GlobalMutexName);
        RunningMutexAttempt local = opener.Open(MutexName);

        bool refused = global.State switch
        {
            RunningMutexState.AlreadyExists => true,
            RunningMutexState.Refused => local.State is RunningMutexState.AlreadyExists,
            _ => false
        };

        if (!refused)
        {
            return new RunningCopyClaim(isRefused: false, global.Handle, local.Handle);
        }

        global.Handle?.Dispose();
        local.Handle?.Dispose();
        return new RunningCopyClaim(isRefused: true, globalHandle: null, localHandle: null);
    }

    private RunningCopyClaim(bool isRefused, IDisposable? globalHandle, IDisposable? localHandle)
    {
        IsRefused = isRefused;
        _globalHandle = globalHandle;
        _localHandle = localHandle;
    }
}
