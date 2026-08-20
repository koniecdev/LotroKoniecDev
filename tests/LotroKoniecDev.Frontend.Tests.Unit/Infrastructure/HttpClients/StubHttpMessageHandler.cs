using System.Net;

namespace LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.HttpClients;

/// <summary>
/// An <see cref="HttpMessageHandler"/> the tests control. It records the last request it saw and returns
/// a prepared response, or throws a transport exception you give it, so the typed client and the
/// delegating handler can be tested without a running API.
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
    /// The body of the last request, read while it is being sent. It has to be read there, because the
    /// typed client disposes the request and its content right after sending, so
    /// <see cref="LastRequest"/>.Content can no longer be read once <c>SendAsync</c> returns.
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
    /// A response with no body but with the headers you pass, for endpoints that return their data in
    /// headers, such as a <c>204</c> with <c>X-Deletion-Finalizes-At</c>.
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
