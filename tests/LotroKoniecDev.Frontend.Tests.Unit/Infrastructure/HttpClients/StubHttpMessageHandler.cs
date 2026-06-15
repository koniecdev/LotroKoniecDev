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
