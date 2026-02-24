namespace LotroKoniecDev.Cli;

internal interface IFileProvider
{
    bool Exists(string? path);
}
