using LotroKoniecDev.Primitives.Enums;

namespace LotroKoniecDev.Domain.Models;

public sealed record DatFileLocation(
    string Path,
    DatFileSource Source,
    string DisplayName);
