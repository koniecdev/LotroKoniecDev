namespace LotroKoniecDev.Domain.Models;

/// <summary>
/// What the stored game version file holds: the forum version, the DAT vnum pair and the hash of
/// the translation file last applied.
/// </summary>
public sealed record StoredVersionInfo(
    string? ForumVersion,
    int? VnumDatFile,
    int? VnumGameData,
    string? TranslationFileHash = null);
