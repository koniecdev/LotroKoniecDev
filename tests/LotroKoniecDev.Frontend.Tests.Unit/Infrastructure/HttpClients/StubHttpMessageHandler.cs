using System.Net;

namespace LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.HttpClients;

/// <summary>
/// Records the last request and returns a prepared response, or throws a given transport exception.
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
    /// Read while the request is sent, because the typed client disposes its content right after.
    /// </summary>
    public string? LastRequestBody { get; private set; }

    public static StubHttpMessageHandler RespondWith(HttpStatusCode statusCode, string jsonBody)
    {
        return new StubHttpMessageHandler(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json")
        });
    }

    /// <summary>
    /// For a flow that loads a resource with a GET and then follows one of its links with a POST.
    /// </summary>
    public static StubHttpMessageHandler RespondWith(
        HttpStatusCode getStatusCode,
        string getJsonBody,
        HttpStatusCode postStatusCode,
        string postJsonBody)
    {
        return new StubHttpMessageHandler(request =>
        {
            bool isPost = request.Method == HttpMethod.Post;
            return new HttpResponseMessage(isPost ? postStatusCode : getStatusCode)
            {
                Content = new StringContent(
                    isPost ? postJsonBody : getJsonBody,
                    System.Text.Encoding.UTF8,
                    "application/json")
            };
        });
    }

    /// <summary>
    /// For a body the other factories cannot write, such as raw bytes or a charset .NET does not know.
    /// </summary>
    public static StubHttpMessageHandler RespondWith(HttpStatusCode statusCode, Func<HttpContent> content)
    {
        return new StubHttpMessageHandler(_ => new HttpResponseMessage(statusCode) { Content = content() });
    }

    public static StubHttpMessageHandler Throw(Exception exception)
    {
        return new StubHttpMessageHandler(_ => throw exception);
    }

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
