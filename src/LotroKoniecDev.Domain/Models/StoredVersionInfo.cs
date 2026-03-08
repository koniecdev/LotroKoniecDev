namespace LotroKoniecDev.Domain.Models;

/// <summary>
/// Version data stored in version.txt: forum version string + DAT vnum pair.
/// </summary>
public sealed record StoredVersionInfo(
    string? ForumVersion,
    int? VnumDatFile,
    int? VnumGameData);
