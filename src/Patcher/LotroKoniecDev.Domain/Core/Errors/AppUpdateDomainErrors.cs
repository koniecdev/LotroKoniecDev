using LotroKoniecDev.Domain.Core.BuildingBlocks;

namespace LotroKoniecDev.Domain.Core.Errors;

public static partial class DomainErrors
{
    /// <summary>The desktop app's update check and update (spec 0016).</summary>
    public static class AppUpdate
    {
        public const string NetworkErrorCode = "AppUpdate.NetworkError";

        public const string TimedOutCode = "AppUpdate.TimedOut";

        public const string RateLimitedCode = "AppUpdate.RateLimited";

        public const string UnexpectedStatusCode = "AppUpdate.UnexpectedStatus";

        public const string HostRefusedCode = "AppUpdate.HostRefused";

        public const string ResponseTooLargeCode = "AppUpdate.ResponseTooLarge";

        public const string InvalidResponseCode = "AppUpdate.InvalidResponse";

        public const string NoDesktopReleaseCode = "AppUpdate.NoDesktopRelease";

        public const string ChecksumFileInvalidCode = "AppUpdate.ChecksumFileInvalid";

        public const string HashMismatchCode = "AppUpdate.HashMismatch";

        public const string FileSystemErrorCode = "AppUpdate.FileSystemError";

        public const string InstallerStartCancelledCode = "AppUpdate.InstallerStartCancelled";

        public const string InstallerStartFailedCode = "AppUpdate.InstallerStartFailed";

        public static Error NetworkError(string message) =>
            Error.IoError(NetworkErrorCode, $"GitHub could not be reached: {message}");

        /// <summary>
        /// No number in the text: the check's own limit, the download's limit and the shared client's
        /// wait for the headers all end here.
        /// </summary>
        public static Error TimedOut =>
            Error.IoError(TimedOutCode, "GitHub did not answer within the time limit.");

        /// <summary>GitHub allows 60 requests an hour per IP address without a token.</summary>
        public static Error RateLimited(int statusCode) =>
            Error.IoError(RateLimitedCode, $"GitHub refused the request with status {statusCode}, most likely its rate limit.");

        /// <summary>GitHub was reached and answered with an error, such as a 404 or a 5xx.</summary>
        public static Error UnexpectedStatus(int statusCode) =>
            Error.IoError(UnexpectedStatusCode, $"GitHub answered with status {statusCode}.");

        public static Error HostRefused(string address) =>
            Error.IoError(HostRefusedCode, $"The address '{address}' is not a GitHub https address, so it was not used.");

        public static Error ResponseTooLarge(long maxBytes) =>
            Error.IoError(ResponseTooLargeCode, $"The response is larger than the limit of {maxBytes} bytes.");

        public static Error InvalidResponse(string message) =>
            Error.IoError(InvalidResponseCode, $"GitHub's answer could not be read: {message}");

        public static Error NoDesktopRelease =>
            Error.NotFound(NoDesktopReleaseCode, "No desktop release with both files is among the releases GitHub listed.");

        public static Error ChecksumFileInvalid(string fileName) =>
            Error.Failure(ChecksumFileInvalidCode, $"The SHA-256 file does not hold one hash for '{fileName}'.");

        public static Error HashMismatch(string fileName) =>
            Error.Failure(HashMismatchCode, $"The downloaded '{fileName}' does not match its SHA-256 file.");

        public static Error FileSystemError(string path, string message) =>
            Error.IoError(FileSystemErrorCode, $"'{path}': {message}");

        public static Error InstallerStartCancelled =>
            Error.Failure(InstallerStartCancelledCode, "The installer was not started: the Windows admin prompt was cancelled.");

        public static Error InstallerStartFailed(string message) =>
            Error.Failure(InstallerStartFailedCode, $"The installer could not be started: {message}");
    }
}
