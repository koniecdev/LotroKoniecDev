using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace LotroKoniecDev.Frontend.Tests.Integration.Shared;

/// <summary>
/// A browser for the in-memory Frontend. It follows no redirect, so a test sees each one, and it keeps a
/// plain cookie jar: it keeps what the server sets and sends it back.
/// </summary>
public sealed partial class CookieJarBrowser : IDisposable
{
    private readonly HttpClient _client;
    private readonly Dictionary<string, string> _cookies = new(StringComparer.Ordinal);

    public void SetCookie(string name, string value)
    {
        _cookies[name] = value;
    }

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
    {
        using (request)
        {
            if (_cookies.Count > 0)
            {
                request.Headers.Add("Cookie", string.Join("; ", _cookies.Select(cookie => $"{cookie.Key}={cookie.Value}")));
            }

            HttpResponseMessage response = await _client.SendAsync(request);
            if (response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? setCookies))
            {
                foreach (string[] pair in setCookies.Select(setCookie => setCookie.Split(';', 2)[0].Split('=', 2)))
                {
                    if (pair[1].Length == 0)
                    {
                        _cookies.Remove(pair[0]);
                    }
                    else
                    {
                        _cookies[pair[0]] = pair[1];
                    }
                }
            }

            return response;
        }
    }

    /// <summary>
    /// Loads a page and reads the antiforgery token from a form on it. The token belongs to the session, so
    /// it serves every form the session posts, not only the one it was read from.
    /// </summary>
    public async Task<string> ReadAntiforgeryTokenAsync(string pageUri)
    {
        using HttpResponseMessage page = await SendAsync(new HttpRequestMessage(HttpMethod.Get, pageUri));
        Match antiforgeryToken = AntiforgeryTokenInput().Match(await page.Content.ReadAsStringAsync());

        return antiforgeryToken.Success
            ? antiforgeryToken.Groups["token"].Value
            : throw new InvalidOperationException($"The page {pageUri} answered {page.StatusCode} with no form.");
    }

    public void Dispose()
    {
        _client.Dispose();
    }

    public CookieJarBrowser(WebApplicationFactory<Program> host)
    {
        _client = host.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
            BaseAddress = new Uri("https://localhost")
        });
    }

    [GeneratedRegex("""name="__RequestVerificationToken" value="(?<token>[^"]+)""")]
    private static partial Regex AntiforgeryTokenInput();
}
