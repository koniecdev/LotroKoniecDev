using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;
using static LotroKoniecDev.Cli.ConsoleWriter;

namespace LotroKoniecDev.Cli.Commands;

internal sealed class BackupManager : IBackupManager
{
    public Result Create(string datPath)
    {
        string backupPath = datPath + ".backup";

        try
        {
            if (File.Exists(backupPath))
            {
                WriteInfo($"Backup already exists: {backupPath}");
            }
            else
            {
                WriteInfo($"Creating backup: {backupPath}");
                File.Copy(datPath, backupPath);
            }

            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure(
                DomainErrors.Backup.CannotCreate(backupPath, ex.Message));
        }
    }

    public void Restore(string datPath)
    {
        string backupPath = datPath + ".backup";

        Console.WriteLine();
        WriteWarning("Restoring from backup...");

        try
        {
            if (!File.Exists(backupPath))
            {
                return;
            }

            File.Copy(backupPath, datPath, overwrite: true);
            WriteInfo("Restored from backup.");
        }
        catch (Exception ex)
        {
            WriteError($"Failed to restore backup: {ex.Message}");
        }
    }
}
