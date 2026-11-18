namespace LotroKoniecDev.DesktopApp.Presentation;

/// <param name="State">What Windows answered.</param>
/// <param name="Handle">
/// The open mutex, or <see langword="null"/> when Windows refused the name or would not let the app open
/// the mutex that holds it.
/// </param>
public sealed record RunningMutexAttempt(RunningMutexState State, IDisposable? Handle);
