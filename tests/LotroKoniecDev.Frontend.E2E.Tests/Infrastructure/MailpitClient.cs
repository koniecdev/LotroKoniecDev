using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LotroKoniecDev.Frontend.E2E.Tests.Infrastructure;

/// <summary>
/// Thin client over the Mailpit HTTP API. The Auth server sends the confirmation link to Mailpit;
/// the flow polls for the message by recipient + subject, then extracts the link (HTML-encoded in the
/// body) so the browser can follow it. The base URL is the host-mapped Mailpit port from the fixture.
/// </summary>
internal static partial class MailpitClient
{
    public static async Task<string> WaitForLinkAsync(
        string mailpitBaseUrl,
        string recipientEmail,
        string subjectContains,
        string linkContains,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using HttpClient client = new() { BaseAddress = new Uri(mailpitBaseUrl, UriKind.Absolute) };
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            string? messageId = await FindLatestMessageIdAsync(client, recipientEmail, subjectContains, cancellationToken);
            if (messageId is not null)
            {
                string body = await GetMessageBodyAsync(client, messageId, cancellationToken);
                string? link = ExtractLink(body, linkContains);
                if (link is not null)
                {
                    return link;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        throw new TimeoutException(
            $"No email to '{recipientEmail}' (subject ~ '{subjectContains}') carrying a link containing " +
            $"'{linkContains}' arrived within {timeout.TotalSeconds:0}s. Is Mailpit ({mailpitBaseUrl}) up?");
    }

    private static async Task<string?> FindLatestMessageIdAsync(
        HttpClient client,
        string recipientEmail,
        string subjectContains,
        CancellationToken cancellationToken)
    {
        string query = Uri.EscapeDataString($"to:{recipientEmail}");
        using HttpResponseMessage response = await client.GetAsync(
            new Uri($"/api/v1/search?query={query}&limit=30", UriKind.Relative), cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, default, cancellationToken);

        if (!document.RootElement.TryGetProperty("messages", out JsonElement messages)
            || messages.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (JsonElement message in messages.EnumerateArray())
        {
            string subject = message.TryGetProperty("Subject", out JsonElement subjectElement)
                ? subjectElement.GetString() ?? string.Empty
                : string.Empty;

            if (subject.Contains(subjectContains, StringComparison.OrdinalIgnoreCase)
                && message.TryGetProperty("ID", out JsonElement idElement))
            {
                return idElement.GetString();
            }
        }

        return null;
    }

    private static async Task<string> GetMessageBodyAsync(
        HttpClient client,
        string messageId,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await client.GetAsync(
            new Uri($"/api/v1/message/{messageId}", UriKind.Relative), cancellationToken);
        response.EnsureSuccessStatusCode();

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, default, cancellationToken);
        JsonElement root = document.RootElement;

        string html = root.TryGetProperty("HTML", out JsonElement htmlElement)
            ? htmlElement.GetString() ?? string.Empty
            : string.Empty;

        if (!string.IsNullOrEmpty(html))
        {
            return html;
        }

        return root.TryGetProperty("Text", out JsonElement textElement)
            ? textElement.GetString() ?? string.Empty
            : string.Empty;
    }

    private static string? ExtractLink(string body, string linkContains)
    {
        foreach (Match match in HrefRegex().Matches(body))
        {
            string href = WebUtility.HtmlDecode(match.Groups[1].Value);
            if (href.Contains(linkContains, StringComparison.OrdinalIgnoreCase))
            {
                return href;
            }
        }

        foreach (Match match in BareUrlRegex().Matches(body))
        {
            string url = WebUtility.HtmlDecode(match.Value);
            if (url.Contains(linkContains, StringComparison.OrdinalIgnoreCase))
            {
                return url;
            }
        }

        return null;
    }

    [GeneratedRegex("href=[\"']([^\"']+)[\"']", RegexOptions.IgnoreCase)]
    private static partial Regex HrefRegex();

    [GeneratedRegex(@"https?://[^\s""'<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex BareUrlRegex();
}
