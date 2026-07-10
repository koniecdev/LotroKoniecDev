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
            // The DAT folder can come from an unauthenticated source (drive scan, user input), so
            // the executable must prove its publisher before it runs — potentially elevated
            // (AUDIT-SEC-02). The read handle is held across the verify → start window, denying
            // writes and renames so the verified bytes cannot be swapped in between.
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
                // UseShellExecute = true allows the launcher to trigger UAC elevation
                // prompts when it needs admin rights for game updates.
                UseShellExecute = true
            });

            if (process is null)
            {
                return Result.Failure(DomainErrors.GameLaunch.LaunchFailed(
                    "Process.Start returned null — the launcher could not be started."));
            }

            // Fire-and-forget: dispose immediately, don't wait for exit.
            // The launcher will restart itself with UAC elevation anyway.
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
