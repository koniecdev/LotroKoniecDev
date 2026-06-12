namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;

#pragma warning disable CA1515
public sealed class TestApiClient
#pragma warning restore CA1515
{
    public TestApiClient(HttpClient httpClient, JsonSerializerOptions jsonSerializerOptions)
    {
        Http = httpClient;
        JsonOptions = jsonSerializerOptions;
    }

    public HttpClient Http { get; }
    public JsonSerializerOptions JsonOptions { get; }
}
