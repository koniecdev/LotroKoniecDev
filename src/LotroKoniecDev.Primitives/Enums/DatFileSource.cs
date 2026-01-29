namespace LotroKoniecDev.Primitives.Enums;

/// <summary>
/// Identifies where a LOTRO DAT file was discovered.
/// </summary>
public enum DatFileSource
{
    StandingStoneGames,
    Steam,
    Registry,
    DiskScan,
    LocalFallback
}
