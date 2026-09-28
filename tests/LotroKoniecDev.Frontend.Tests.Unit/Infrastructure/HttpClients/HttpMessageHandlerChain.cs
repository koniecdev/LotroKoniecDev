namespace LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.HttpClients;

/// <summary>
/// Lists a handler and every handler inside it, outermost first, so a test can check the primary
/// handler at the end of a real registration.
/// </summary>
internal static class HttpMessageHandlerChain
{
    public static List<HttpMessageHandler> From(HttpMessageHandler? handler)
    {
        List<HttpMessageHandler> chain = [];
        while (handler is not null)
        {
            chain.Add(handler);
            handler = handler is DelegatingHandler delegatingHandler ? delegatingHandler.InnerHandler : null;
        }

        return chain;
    }
}
