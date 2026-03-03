namespace LotroKoniecDev.Application.Features.GameLaunching;

public sealed record GameLaunchingResponse(
    string? ForumVersion,
    bool UpdateWasDetected,
    int GameExitCode,
    TimeSpan PlayTime
);
