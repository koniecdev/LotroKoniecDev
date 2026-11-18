using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Features.AppUpdating;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;

namespace LotroKoniecDev.Infrastructure.AppUpdating;

/// <summary>
/// Opens the downloaded installer read-only, sharing read access only and not delete, so no other
/// program can write, delete or rename it while it is held. Windows starts the installer with admin
/// rights from the held file. Inno Setup reads the file again by path later, after the app has
/// closed, and the hold does not cover those reads (spec 0016).
/// </summary>
public sealed class InstallerFileOpener : IInstallerFileOpener
{
    private readonly Func<ProcessStartInfo, Process?> _startProcess;

    internal static ProcessStartInfo CreateStartInfo(string installerPath, string arguments) =>
        new()
        {
            FileName = installerPath,
            Arguments = arguments,
            WorkingDirectory = Path.GetDirectoryName(installerPath) ?? string.Empty,
            Verb = DesktopReleasePolicy.InstallerStartVerb,
            // A verb works only through the shell.
            UseShellExecute = true
        };

    public Result<IOpenedInstaller> Open(string installerPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installerPath);

        string fullPath = Path.GetFullPath(installerPath);
        FileStream? stream = null;
        try
        {
            stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            string sha256Hex = Convert.ToHexStringLower(SHA256.HashData(stream));

            OpenedInstaller opened = new(fullPath, stream, sha256Hex, _startProcess);
            stream = null;
            return Result.Success<IOpenedInstaller>(opened);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Failure<IOpenedInstaller>(DomainErrors.AppUpdate.FileSystemError(fullPath, ex.Message));
        }
        finally
        {
            stream?.Dispose();
        }
    }

    public InstallerFileOpener()
        : this(Process.Start)
    {
    }

    /// <summary>Tests replace the start, so each answer Windows can give is tested with no prompt.</summary>
    internal InstallerFileOpener(Func<ProcessStartInfo, Process?> startProcess)
    {
        _startProcess = startProcess;
    }

    private sealed class OpenedInstaller : IOpenedInstaller
    {
        /// <summary>Win32 <c>ERROR_CANCELLED</c>: the player said no to the admin prompt.</summary>
        private const int ErrorCancelled = 1223;

        private readonly string _path;
        private readonly FileStream _stream;
        private readonly Func<ProcessStartInfo, Process?> _startProcess;

        public string Sha256Hex { get; }

        public Result Start(string arguments)
        {
            ArgumentNullException.ThrowIfNull(arguments);

            try
            {
                using Process? process = _startProcess(CreateStartInfo(_path, arguments));

                return process is null
                    ? Result.Failure(DomainErrors.AppUpdate.InstallerStartFailed("Windows started no process."))
                    : Result.Success();
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
            {
                return Result.Failure(DomainErrors.AppUpdate.InstallerStartCancelled);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
            {
                return Result.Failure(DomainErrors.AppUpdate.InstallerStartFailed(ex.Message));
            }
        }

        public void Dispose() => _stream.Dispose();

        public OpenedInstaller(
            string path, FileStream stream, string sha256Hex, Func<ProcessStartInfo, Process?> startProcess)
        {
            _path = path;
            _stream = stream;
            Sha256Hex = sha256Hex;
            _startProcess = startProcess;
        }
    }
}
