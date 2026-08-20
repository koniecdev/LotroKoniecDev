using System.Diagnostics;
using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;

namespace LotroKoniecDev.Infrastructure.GameLaunching;

public sealed class GameLauncher : IGameLauncher
{
    private const string LauncherExecutable = "LotroLauncher.exe";

    private readonly ILauncherSignatureVerifier _signatureVerifier;

    public GameLauncher(ILauncherSignatureVerifier signatureVerifier)
    {
        _signatureVerifier = signatureVerifier;
    }

    public Result Launch(string datFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datFilePath);

        string launcherPath = ResolveLauncherPath(datFilePath);

        if (!File.Exists(launcherPath))
        {
            return Result.Failure(DomainErrors.GameLaunch.LauncherNotFound(launcherPath));
        }

        try
        {
            // The DAT folder can come from a source we do not trust, such as a drive scan or user
            // input, so the executable has to prove who published it before it runs, possibly with
            // admin rights (AUDIT-SEC-02). We keep the read handle open from the check until the
            // start, which blocks writes and renames, so nobody can swap the bytes in between.
            using FileStream launcherGuard = new(
                launcherPath, FileMode.Open, FileAccess.Read, FileShare.Read);

            Result signatureResult = _signatureVerifier.VerifySignature(launcherPath);
            if (signatureResult.IsFailure)
            {
                return signatureResult;
            }

            Process? process = Process.Start(new ProcessStartInfo
            {
                FileName = launcherPath,
                WorkingDirectory = Path.GetDirectoryName(launcherPath) ?? string.Empty,
                // UseShellExecute = true lets the launcher raise a UAC prompt when it needs admin
                // rights for a game update.
                UseShellExecute = true
            });

            if (process is null)
            {
                return Result.Failure(DomainErrors.GameLaunch.LaunchFailed(
                    "Process.Start returned null — the launcher could not be started."));
            }

            // We do not wait for the launcher to exit. It restarts itself with elevation anyway.
            process.Dispose();
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure(DomainErrors.GameLaunch.LaunchFailed(ex.Message));
        }
    }

    private static string ResolveLauncherPath(string datFilePath)
    {
        if (Directory.Exists(datFilePath))
        {
            return Path.Combine(datFilePath, LauncherExecutable);
        }

        if (File.Exists(datFilePath))
        {
            string dirPath = Path.GetDirectoryName(datFilePath) ?? string.Empty;
            return Path.Combine(dirPath, LauncherExecutable);
        }

        return Path.Combine(datFilePath, LauncherExecutable);
    }
}
