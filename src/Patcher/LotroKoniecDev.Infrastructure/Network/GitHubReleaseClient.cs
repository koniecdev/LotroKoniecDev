using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LotroKoniecDev.Application;
using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Features.AppUpdating;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;

namespace LotroKoniecDev.Infrastructure.Network;

/// <summary>
/// Talks to GitHub for the desktop app's update (spec 0016). It uses the shared client, which follows
/// redirects, and checks the final address and the status before it reads any byte of a body: GitHub
/// sends release downloads on to another GitHub host. No secret travels with these requests.
/// </summary>
public sealed class GitHubReleaseClient : IGitHubReleaseClient
{
    private const string GitHubJsonMediaType = "application/vnd.github+json";

    private const int CopyBufferBytes = 81920;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly HttpClient _httpClient;
    private readonly TimeSpan _checkTimeLimit;
    private readonly TimeSpan _downloadTimeLimit;
    private readonly Func<string, Stream> _openTarget;

    public async Task<Result<IReadOnlyList<GitHubRelease>>> ListReleasesAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_checkTimeLimit);

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, DesktopReleasePolicy.ReleasesListUri);
            // No X-GitHub-Api-Version header: GitHub answers 410 Gone to a retired version, and a shipped
            // app would then never update again. The five fields read here are the same in every version.
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(GitHubJsonMediaType));

            using HttpResponseMessage response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);

            Result answer = CheckAnswer(response, DesktopReleasePolicy.ReleasesListUri);
            if (answer.IsFailure)
            {
                return Result.Failure<IReadOnlyList<GitHubRelease>>(answer.Error);
            }

            Maybe<string> body = await BoundedResponseReader.TryReadAsStringAsync(
                response.Content, DesktopReleasePolicy.MaxReleaseListBytes, timeoutCts.Token);
            if (body.HasNoValue)
            {
                return Result.Failure<IReadOnlyList<GitHubRelease>>(
                    DomainErrors.AppUpdate.ResponseTooLarge(DesktopReleasePolicy.MaxReleaseListBytes));
            }

            ReleaseDto?[]? releases = JsonSerializer.Deserialize<ReleaseDto?[]>(body.Value, JsonOptions);
            if (releases is null || releases.Any(release => release is null || release.Assets?.Any(asset => asset is null) == true))
            {
                return Result.Failure<IReadOnlyList<GitHubRelease>>(
                    DomainErrors.AppUpdate.InvalidResponse("the release list, a release or an asset is null."));
            }

            return Result.Success<IReadOnlyList<GitHubRelease>>(releases.Select(release => ToRelease(release!)).ToList());
        }
        catch (JsonException ex)
        {
            return Result.Failure<IReadOnlyList<GitHubRelease>>(DomainErrors.AppUpdate.InvalidResponse(ex.Message));
        }
        catch (HttpRequestException ex)
        {
            return Result.Failure<IReadOnlyList<GitHubRelease>>(DomainErrors.AppUpdate.NetworkError(ex.Message));
        }
        catch (OperationCanceledException)
        {
            return Result.Failure<IReadOnlyList<GitHubRelease>>(
                DomainErrors.AppUpdate.TimedOut);
        }
    }

    public async Task<Result<string>> DownloadTextAsync(Uri uri, long maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);

        if (!DesktopReleasePolicy.IsAllowedHost(uri))
        {
            return Result.Failure<string>(DomainErrors.AppUpdate.HostRefused(uri.ToString()));
        }

        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_checkTimeLimit);

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, uri);
            using HttpResponseMessage response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);

            Result answer = CheckAnswer(response, uri);
            if (answer.IsFailure)
            {
                return Result.Failure<string>(answer.Error);
            }

            Maybe<string> body = await BoundedResponseReader.TryReadAsStringAsync(
                response.Content, maxBytes, timeoutCts.Token);
            return body.HasValue
                ? Result.Success(body.Value)
                : Result.Failure<string>(DomainErrors.AppUpdate.ResponseTooLarge(maxBytes));
        }
        catch (HttpRequestException ex)
        {
            return Result.Failure<string>(DomainErrors.AppUpdate.NetworkError(ex.Message));
        }
        catch (OperationCanceledException)
        {
            return Result.Failure<string>(DomainErrors.AppUpdate.TimedOut);
        }
    }

    public async Task<Result> DownloadFileAsync(
        Uri uri,
        string destinationPath,
        long maxBytes,
        IProgress<OperationProgress> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(progress);

        if (!DesktopReleasePolicy.IsAllowedHost(uri))
        {
            return Result.Failure(DomainErrors.AppUpdate.HostRefused(uri.ToString()));
        }

        if (File.Exists(destinationPath))
        {
            return Result.Failure(DomainErrors.AppUpdate.FileSystemError(destinationPath, "the file already exists."));
        }

        // Only a file this download created is deleted: one that appeared between the check above and
        // the open belongs to someone else.
        StrongBox<bool> fileCreated = new(false);
        Result result = await TryDownloadFileAsync(uri, destinationPath, maxBytes, progress, fileCreated, cancellationToken);
        if (result.IsFailure && fileCreated.Value)
        {
            DeletePartialFile(destinationPath);
        }

        return result;
    }

    public GitHubReleaseClient(HttpClient httpClient)
        : this(httpClient, DesktopReleasePolicy.CheckTimeLimit, DesktopReleasePolicy.DownloadTimeLimit)
    {
    }

    /// <summary>
    /// For tests: shorter limits, because a body that stalls after the headers ends only here, and a
    /// target file that fails, because no test can fill the disk.
    /// </summary>
    internal GitHubReleaseClient(
        HttpClient httpClient,
        TimeSpan checkTimeLimit,
        TimeSpan downloadTimeLimit,
        Func<string, Stream>? openTarget = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _checkTimeLimit = checkTimeLimit;
        _downloadTimeLimit = downloadTimeLimit;
        _openTarget = openTarget ?? OpenNewFile;
    }

    private async Task<Result> TryDownloadFileAsync(
        Uri uri,
        string destinationPath,
        long maxBytes,
        IProgress<OperationProgress> progress,
        StrongBox<bool> fileCreated,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_downloadTimeLimit);

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, uri);
            using HttpResponseMessage response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);

            Result answer = CheckAnswer(response, uri);
            if (answer.IsFailure)
            {
                return answer;
            }

            long? declaredLength = response.Content.Headers.ContentLength;
            if (declaredLength > maxBytes)
            {
                return Result.Failure(DomainErrors.AppUpdate.ResponseTooLarge(maxBytes));
            }

            // The body first: a file opened before it would stay open, and undeletable on Windows,
            // when the wait for the body ends.
            await using Stream source = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
            Result<Stream> target = OpenTarget(destinationPath);
            if (target.IsFailure)
            {
                return target;
            }

            fileCreated.Value = true;
            return await CopyAndCloseAsync(
                source, target.Value, destinationPath, maxBytes, declaredLength, progress, timeoutCts.Token);
        }
        catch (HttpRequestException ex)
        {
            return Result.Failure(DomainErrors.AppUpdate.NetworkError(ex.Message));
        }
        catch (OperationCanceledException)
        {
            return Result.Failure(DomainErrors.AppUpdate.TimedOut);
        }
        catch (IOException ex)
        {
            // A body read that broke off. A write, flush or close of the file ends in CopyAndCloseAsync.
            return Result.Failure(DomainErrors.AppUpdate.NetworkError(ex.Message));
        }
    }

    /// <summary>
    /// <see cref="FileMode.CreateNew"/>, so a download never writes over a file it did not create, and
    /// a failed download deletes only the file it created.
    /// </summary>
    private static Stream OpenNewFile(string destinationPath) =>
        new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferBytes, useAsync: true);

    private Result<Stream> OpenTarget(string destinationPath)
    {
        try
        {
            return Result.Success(_openTarget(destinationPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Failure<Stream>(DomainErrors.AppUpdate.FileSystemError(destinationPath, ex.Message));
        }
    }

    /// <summary>
    /// Copies the body and closes the file in one place. A file stream keeps the bytes of a failed
    /// write in its buffer and fails again when it is closed, so a close outside this method would
    /// turn a full disk into a network error.
    /// </summary>
    private static async Task<Result> CopyAndCloseAsync(
        Stream source,
        Stream target,
        string destinationPath,
        long maxBytes,
        long? declaredLength,
        IProgress<OperationProgress> progress,
        CancellationToken cancellationToken)
    {
        Result copied;
        try
        {
            copied = await CopyWithLimitAsync(
                source, target, destinationPath, maxBytes, declaredLength, progress, cancellationToken);
        }
        catch
        {
            // A failed read or the time limit decides the answer; a close that fails too must not hide it.
            await CloseAsync(target, destinationPath);
            throw;
        }

        Result closed = await CloseAsync(target, destinationPath);
        return copied.IsFailure ? copied : closed;
    }

    private static async Task<Result> CloseAsync(Stream target, string destinationPath)
    {
        try
        {
            await target.DisposeAsync();
            return Result.Success();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Failure(DomainErrors.AppUpdate.FileSystemError(destinationPath, ex.Message));
        }
    }

    /// <summary>
    /// Counts the bytes as they arrive, because a body sent in chunks, or one that lies about its
    /// length, has no trustworthy size up front. Progress is in KiB, so a 256 MiB file fits an
    /// <see cref="int"/>, and is reported only when the percent changes.
    /// </summary>
    private static async Task<Result> CopyWithLimitAsync(
        Stream source,
        Stream target,
        string destinationPath,
        long maxBytes,
        long? declaredLength,
        IProgress<OperationProgress> progress,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[CopyBufferBytes];
        long copied = 0;
        int lastPercent = -1;

        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            copied += read;
            if (copied > maxBytes)
            {
                return Result.Failure(DomainErrors.AppUpdate.ResponseTooLarge(maxBytes));
            }

            Result written = await WriteAsync(target, buffer.AsMemory(0, read), destinationPath, cancellationToken);
            if (written.IsFailure)
            {
                return written;
            }

            if (declaredLength is > 0)
            {
                int percent = (int)(copied * 100 / declaredLength.Value);
                if (percent != lastPercent)
                {
                    lastPercent = percent;
                    progress.Report(new OperationProgress((int)(copied / 1024), (int)(declaredLength.Value / 1024)));
                }
            }
        }

        Result flushed = await FlushAsync(target, destinationPath, cancellationToken);
        if (flushed.IsFailure)
        {
            return flushed;
        }

        progress.Report(new OperationProgress(1, 1));
        return Result.Success();
    }

    private static async Task<Result> WriteAsync(
        Stream target, ReadOnlyMemory<byte> bytes, string destinationPath, CancellationToken cancellationToken)
    {
        try
        {
            await target.WriteAsync(bytes, cancellationToken);
            return Result.Success();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Failure(DomainErrors.AppUpdate.FileSystemError(destinationPath, ex.Message));
        }
    }

    private static async Task<Result> FlushAsync(Stream target, string destinationPath, CancellationToken cancellationToken)
    {
        try
        {
            await target.FlushAsync(cancellationToken);
            return Result.Success();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Failure(DomainErrors.AppUpdate.FileSystemError(destinationPath, ex.Message));
        }
    }

    /// <summary>
    /// The address the answer really came from, after any redirects, must be GitHub over https. A
    /// redirect that came back unfollowed (https to http, which .NET refuses to follow) is refused too.
    /// </summary>
    private static Result CheckAnswer(HttpResponseMessage response, Uri requestedUri)
    {
        Uri? answeredFrom = response.RequestMessage?.RequestUri;
        if (!DesktopReleasePolicy.IsAllowedHost(answeredFrom))
        {
            return Result.Failure(DomainErrors.AppUpdate.HostRefused((answeredFrom ?? requestedUri).ToString()));
        }

        int status = (int)response.StatusCode;
        if (status is >= 300 and < 400)
        {
            string target = response.Headers.Location?.ToString() ?? "(no address)";
            return Result.Failure(DomainErrors.AppUpdate.HostRefused(target));
        }

        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            return Result.Failure(DomainErrors.AppUpdate.RateLimited(status));
        }

        return response.IsSuccessStatusCode
            ? Result.Success()
            : Result.Failure(DomainErrors.AppUpdate.UnexpectedStatus(status));
    }

    private static GitHubRelease ToRelease(ReleaseDto release) =>
        new(
            release.TagName ?? string.Empty,
            release.Draft,
            release.Prerelease,
            (release.Assets ?? []).Select(asset => ToAsset(asset!)).ToList());

    private static GitHubReleaseAsset ToAsset(AssetDto asset) =>
        new(
            asset.Name ?? string.Empty,
            Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out Uri? downloadUri) ? downloadUri : null);

    private static void DeletePartialFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The folder is removed at the next start anyway (AppUpdateWorkspace).
        }
    }

    private sealed record ReleaseDto(string? TagName, bool Draft, bool Prerelease, AssetDto?[]? Assets);

    private sealed record AssetDto(string? Name, string? BrowserDownloadUrl);
}
