using LotroKoniecDev.Primitives.Enums;

namespace LotroKoniecDev.Domain.Models;

public sealed record DatFileLocation(
    string Path,
    DatFileSource Source,
    string DisplayName)
{
    /// <summary>
    /// Nothing checks the game launcher, so how the folder was found is the guard (ADR-0069 §2). Only
    /// a known install location is used with no question; every other source, a new one included,
    /// waits for the player (the #1292 allow-list).
    /// </summary>
    public bool NeedsConfirmation =>
        Source is not (DatFileSource.StandingStoneGames or DatFileSource.Steam or DatFileSource.Registry);
}
