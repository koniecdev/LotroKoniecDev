using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace LotroKoniecDev.AuthSystem.Infrastructure.PwnedPasswords;

internal sealed partial class PwnedPasswordChecker : IPwnedPasswordChecker
{
    internal const int HashPrefixLength = 5;

    internal const string RangePath = "range/";

    internal const string AddPaddingHeader = "Add-Padding";

    /// <summary>
    /// Long enough that a form posted twice, or the same password typed again after an error, gets its
    /// answer without a second call. Only a real answer is kept: a failed call is tried again next time.
    /// </summary>
    internal static readonly TimeSpan VerdictLifetime = TimeSpan.FromMinutes(5);

    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _verdictCache;
    private readonly ILogger<PwnedPasswordChecker> _logger;

    public PwnedPasswordChecker(
        HttpClient httpClient,
        IMemoryCache verdictCache,
        ILogger<PwnedPasswordChecker> logger)
    {
        _httpClient = httpClient;
        _verdictCache = verdictCache;
        _logger = logger;
    }

    public async Task<PwnedPasswordVerdict> CheckAsync(string password, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(password);

        string hash = HashForRangeApi(password);
        VerdictCacheKey cacheKey = new(hash);

        if (_verdictCache.TryGetValue(cacheKey, out PwnedPasswordVerdict cachedVerdict))
        {
            return cachedVerdict;
        }

        PwnedPasswordVerdict verdict = await AskRangeApiAsync(
            hash[..HashPrefixLength],
            hash[HashPrefixLength..],
            cancellationToken);

        if (verdict is not PwnedPasswordVerdict.Unavailable)
        {
            _verdictCache.Set(cacheKey, verdict, VerdictLifetime);
        }

        return verdict;
    }

    /// <summary>
    /// Reads one line of the answer per known suffix, in the form <c>SUFFIX:COUNT</c>. A padding line
    /// always has a count of zero and stands for no real password, so a match there is not a breach.
    /// </summary>
    internal static bool ContainsBreachedSuffix(string rangeBody, string hashSuffix)
    {
        foreach (ReadOnlySpan<char> line in rangeBody.AsSpan().EnumerateLines())
        {
            int separator = line.IndexOf(':');

            if (separator < 0 || !line[..separator].Trim().Equals(hashSuffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return long.TryParse(
                       line[(separator + 1)..].Trim(),
                       NumberStyles.None,
                       CultureInfo.InvariantCulture,
                       out long breachCount)
                   && breachCount > 0;
        }

        return false;
    }

    /// <summary>
    /// SHA-1 is what the range API is keyed by, so it is the only hash it can answer for. It is never
    /// stored and never used to verify a password.
    /// </summary>
    internal static string HashForRangeApi(string password) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(password)));

    private async Task<PwnedPasswordVerdict> AskRangeApiAsync(
        string hashPrefix,
        string hashSuffix,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(RangePath + hashPrefix, UriKind.Relative));

        // Every answer is padded to a random length, so its size does not tell an observer on the wire
        // which bucket was asked for.
        request.Headers.Add(AddPaddingHeader, "true");

        try
        {
            using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                LogUnexpectedStatus(_logger, (int)response.StatusCode);
                return PwnedPasswordVerdict.Unavailable;
            }

            // The answer is ASCII hex and digits. It is decoded here instead of by the charset the answer
            // names, because an unknown charset would throw instead of failing the check (#1036).
            byte[] body = await response.Content.ReadAsByteArrayAsync(cancellationToken);

            return ContainsBreachedSuffix(Encoding.UTF8.GetString(body), hashSuffix)
                ? PwnedPasswordVerdict.Breached
                : PwnedPasswordVerdict.NotFound;
        }
        catch (HttpRequestException exception)
        {
            LogUnreachable(_logger, exception);
            return PwnedPasswordVerdict.Unavailable;
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            LogTimedOut(_logger, exception);
            return PwnedPasswordVerdict.Unavailable;
        }
    }

    [LoggerMessage(EventId = EventIds.PwnedPasswordsUnexpectedStatus, Level = LogLevel.Warning, Message = "The Pwned Passwords range API answered {StatusCode}, so the password was not checked against known breaches")]
    private static partial void LogUnexpectedStatus(ILogger logger, int statusCode);

    [LoggerMessage(EventId = EventIds.PwnedPasswordsUnreachable, Level = LogLevel.Warning, Message = "The Pwned Passwords range API could not be reached, so the password was not checked against known breaches")]
    private static partial void LogUnreachable(ILogger logger, Exception exception);

    [LoggerMessage(EventId = EventIds.PwnedPasswordsTimedOut, Level = LogLevel.Warning, Message = "The Pwned Passwords range API did not answer in time, so the password was not checked against known breaches")]
    private static partial void LogTimedOut(ILogger logger, Exception exception);

    /// <summary>
    /// Its own key type, so no other entry in the shared memory cache can ever collide with a verdict.
    /// </summary>
    private readonly record struct VerdictCacheKey(string Hash);
}
