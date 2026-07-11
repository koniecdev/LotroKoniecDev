using System.Net;

namespace LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.HttpClients;

/// <summary>
/// A controllable <see cref="HttpMessageHandler"/> for the HTTP seam: it records the last request it
/// saw and returns a canned response (or throws a supplied transport exception), so the typed client
/// and delegating handler can be tested without a live API.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    private StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _responder = responder;
    }

    public HttpRequestMessage? LastRequest { get; private set; }

    /// <summary>
    /// The body of the last request, captured at send time. Read here because the typed client disposes
    /// the request (and its content) right after sending, so <see cref="LastRequest"/>.Content can no
    /// longer be read once <c>SendAsync</c> returns.
    /// </summary>
    public string? LastRequestBody { get; private set; }

    public static StubHttpMessageHandler RespondWith(HttpStatusCode statusCode, string jsonBody)
    {
        return new StubHttpMessageHandler(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json")
        });
    }

    public static StubHttpMessageHandler Throw(Exception exception)
    {
        return new StubHttpMessageHandler(_ => throw exception);
    }

    /// <summary>
    /// A body-less response carrying custom headers — for endpoints whose success payload travels in
    /// response headers (e.g. <c>204</c> + <c>X-Deletion-Finalizes-At</c>).
    /// </summary>
    public static StubHttpMessageHandler RespondWithHeaders(
        HttpStatusCode statusCode,
        IReadOnlyDictionary<string, string> headers)
    {
        return new StubHttpMessageHandler(_ =>
        {
            HttpResponseMessage response = new(statusCode);
            foreach (KeyValuePair<string, string> header in headers)
            {
                response.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            return response;
        });
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        LastRequest = request;
        LastRequestBody = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);
        return _responder(request);
    }
}
