namespace LotroKoniecDev.Domain.Models;

/// <summary>
/// What version.txt holds: the forum version string and the DAT vnum pair.
/// </summary>
public sealed record StoredVersionInfo(
    string? ForumVersion,
    int? VnumDatFile,
    int? VnumGameData,
    string? TranslationFileHash = null);
