namespace LotroKoniecDev.DesktopApp.Presentation;

/// <param name="State">What Windows answered.</param>
/// <param name="Handle">The open mutex, or <see langword="null"/> when Windows refused it.</param>
public sealed record RunningMutexAttempt(RunningMutexState State, IDisposable? Handle);
