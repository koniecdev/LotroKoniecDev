using System.Net;
using System.Net.Http.Headers;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.PwnedPasswords;

/// <summary>
/// Stands in for the Pwned Passwords range API. It keeps a copy of every request it receives, because
/// what leaves the process is exactly what the tests have to prove.
/// </summary>
internal sealed class StubRangeApiHandler : HttpMessageHandler
{
    private readonly Func<CancellationToken, Task<HttpResponseMessage>> _answer;
    private readonly List<SentRequest> _requests = [];
    private readonly Lock _gate = new();

    private StubRangeApiHandler(Func<CancellationToken, Task<HttpResponseMessage>> answer)
    {
        _answer = answer;
    }

    public IReadOnlyList<SentRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    public static StubRangeApiHandler Answering(string body) =>
        Answering(body, "text/plain; charset=utf-8");

    public static StubRangeApiHandler Answering(string body, string contentType) =>
        new(_ =>
        {
            ByteArrayContent content = new(System.Text.Encoding.UTF8.GetBytes(body));
            content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });

    public static StubRangeApiHandler AnsweringStatus(HttpStatusCode statusCode) =>
        new(_ => Task.FromResult(new HttpResponseMessage(statusCode) { Content = new StringContent(string.Empty) }));

    public static StubRangeApiHandler Throwing(Exception exception) =>
        new(_ => Task.FromException<HttpResponseMessage>(exception));

    public static StubRangeApiHandler NeverAnswering() =>
        new(async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("An infinite delay ended without being cancelled.");
        });

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        SentRequest sent = new(
            request.Method,
            request.RequestUri,
            string.Join('\n', request.Headers.Select(header => $"{header.Key}: {string.Join(',', header.Value)}")),
            request.Content is not null);

        lock (_gate)
        {
            _requests.Add(sent);
        }

        return _answer(cancellationToken);
    }

    internal sealed record SentRequest(HttpMethod Method, Uri? Uri, string Headers, bool HasContent);
}
