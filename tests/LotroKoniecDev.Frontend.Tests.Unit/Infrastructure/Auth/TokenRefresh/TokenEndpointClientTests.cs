using System.Net;
using System.Net.Http.Headers;
using LotroKoniecDev.Frontend.Infrastructure.Auth.TokenRefresh;
using LotroKoniecDev.Frontend.Settings;
using LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Tests.Unit.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.Auth.TokenRefresh;

public sealed class TokenEndpointClientTests
{
    // Built rather than written out, so no secret scanner mistakes test data for a token.
    private static readonly string AccessToken = new('a', 40);

    private const string AuthBaseUrl = "https://auth.lotro.test/";
    private const string RefreshToken = "the-refresh-token";

    [Fact]
    public async Task RefreshAsync_WhenTheAuthApiAnswersWithTokens_ReturnsThem()
    {
        using HttpClient httpClient = CreateHttpClient(StubHttpMessageHandler.RespondWith(
            HttpStatusCode.OK,
            $$"""{"access_token":"{{AccessToken}}","expires_in":3600}"""));
        TokenEndpointClient client = CreateClient(httpClient);

        TokenResponse? response = await client.RefreshAsync(RefreshToken);

        response.ShouldNotBeNull().AccessToken.ShouldBe(AccessToken);
    }

    /// <summary>
    /// #899: the primary handler follows no redirect, so a redirect reaches this client as it is. It has
    /// to count as a failed refresh, the same as any other answer that carries no tokens.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.SeeOther)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task RefreshAsync_WhenTheAuthApiAnswersWithARedirect_ReturnsNull(HttpStatusCode statusCode)
    {
        using HttpClient httpClient = CreateHttpClient(StubHttpMessageHandler.RespondWithHeaders(
            statusCode,
            new Dictionary<string, string> { ["Location"] = "https://attacker.example/connect/token" }));
        TokenEndpointClient client = CreateClient(httpClient);

        TokenResponse? response = await client.RefreshAsync(RefreshToken);

        response.ShouldBeNull();
    }

    /// <summary>
    /// #914: the refused refresh ends the session either way, so the warning is the only place in the
    /// frontend that says why. The auth API's reason has to reach it.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "invalid_grant", "The refresh token is no longer valid.")]
    [InlineData(HttpStatusCode.Unauthorized, "invalid_client", "The specified client credentials are invalid.")]
    public async Task RefreshAsync_WhenTheAuthApiRefusesWithTheOAuthErrorBody_LogsOneWarningWithTheStatusAndTheReason(
        HttpStatusCode statusCode,
        string error,
        string errorDescription)
    {
        using HttpClient httpClient = CreateHttpClient(StubHttpMessageHandler.RespondWith(
            statusCode,
            $$"""{"error":"{{error}}","error_description":"{{errorDescription}}"}"""));
        using CapturingLoggerProvider logs = new();
        using LoggerFactory loggerFactory = new([logs]);
        TokenEndpointClient client = CreateClient(httpClient, loggerFactory.CreateLogger<TokenEndpointClient>());

        TokenResponse? response = await client.RefreshAsync(RefreshToken);

        response.ShouldBeNull();
        CapturingLoggerProvider.LogEntry entry = logs.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Message.ShouldContain(((int)statusCode).ToString(System.Globalization.CultureInfo.InvariantCulture));
        entry.Message.ShouldContain(error);
        entry.Message.ShouldContain(errorDescription);
    }

    [Fact]
    public async Task RefreshAsync_WhenTheOAuthErrorBodyHasNoDescription_LogsTheStatusAndTheError()
    {
        using HttpClient httpClient = CreateHttpClient(StubHttpMessageHandler.RespondWith(
            HttpStatusCode.BadRequest,
            """{"error":"invalid_grant"}"""));
        using CapturingLoggerProvider logs = new();
        using LoggerFactory loggerFactory = new([logs]);
        TokenEndpointClient client = CreateClient(httpClient, loggerFactory.CreateLogger<TokenEndpointClient>());

        TokenResponse? response = await client.RefreshAsync(RefreshToken);

        response.ShouldBeNull();
        CapturingLoggerProvider.LogEntry entry = logs.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Message.ShouldContain("400");
        entry.Message.ShouldContain("invalid_grant");
    }

    /// <summary>
    /// A proxy error page, an empty answer or an older ProblemDetails body carries no OAuth reason. The
    /// warning still names the status, and the refresh fails quietly instead of throwing.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<html><body>502 Bad Gateway</body></html>")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"invalid_grant\"")]
    [InlineData("""{"title":"Bad Request","status":400,"detail":"The refresh token is no longer valid."}""")]
    [InlineData("""{"error":5,"error_description":"not a string"}""")]
    [InlineData("""{"error":"","error_description":"no error code"}""")]
    [InlineData("""{"error":"   ","error_description":"no error code"}""")]
    [InlineData("""{"error":null,"error_description":"no error code"}""")]
    [InlineData("""{"error":"invalid_grant","error_description":""")]
    public async Task RefreshAsync_WhenTheRefusalBodyIsNotTheOAuthShape_LogsOneWarningWithTheStatusAlone(string body)
    {
        using HttpClient httpClient = CreateHttpClient(StubHttpMessageHandler.RespondWith(HttpStatusCode.BadRequest, body));
        using CapturingLoggerProvider logs = new();
        using LoggerFactory loggerFactory = new([logs]);
        TokenEndpointClient client = CreateClient(httpClient, loggerFactory.CreateLogger<TokenEndpointClient>());

        TokenResponse? response = await client.RefreshAsync(RefreshToken);

        response.ShouldBeNull();
        CapturingLoggerProvider.LogEntry entry = logs.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Message.ShouldBe("Refresh token grant failed with status 400.");
    }

    [Fact]
    public async Task RefreshAsync_WhenTheRefusalCarriesARedirectWithNoBody_LogsOneWarningWithTheStatusAlone()
    {
        using HttpClient httpClient = CreateHttpClient(StubHttpMessageHandler.RespondWithHeaders(
            HttpStatusCode.Found,
            new Dictionary<string, string> { ["Location"] = "https://attacker.example/connect/token" }));
        using CapturingLoggerProvider logs = new();
        using LoggerFactory loggerFactory = new([logs]);
        TokenEndpointClient client = CreateClient(httpClient, loggerFactory.CreateLogger<TokenEndpointClient>());

        await client.RefreshAsync(RefreshToken);

        CapturingLoggerProvider.LogEntry entry = logs.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Message.ShouldBe("Refresh token grant failed with status 302.");
    }

    /// <summary>
    /// Only the two OAuth fields may reach the log. A body that repeats the refresh token anywhere else
    /// must not carry it into the warning.
    /// </summary>
    [Theory]
    [InlineData($$"""{"error":"invalid_grant","error_description":"Refused.","refresh_token":"{{RefreshToken}}"}""")]
    [InlineData("grant_type=refresh_token&refresh_token=" + RefreshToken)]
    public async Task RefreshAsync_WhenTheRefusalBodyRepeatsTheRefreshToken_NeverLogsIt(string body)
    {
        using HttpClient httpClient = CreateHttpClient(StubHttpMessageHandler.RespondWith(HttpStatusCode.BadRequest, body));
        using CapturingLoggerProvider logs = new();
        using LoggerFactory loggerFactory = new([logs]);
        TokenEndpointClient client = CreateClient(httpClient, loggerFactory.CreateLogger<TokenEndpointClient>());

        await client.RefreshAsync(RefreshToken);

        CapturingLoggerProvider.LogEntry entry = logs.Entries.ShouldHaveSingleItem();
        entry.Message.ShouldNotContain(RefreshToken);
    }

    [Theory]
    [InlineData(200, "")]
    [InlineData(201, "...")]
    public async Task RefreshAsync_WhenTheErrorDescriptionReachesTheCap_LogsItWholeOrCutsIt(
        int length,
        string expectedSuffix)
    {
        string errorDescription = new('d', length);
        using HttpClient httpClient = CreateHttpClient(StubHttpMessageHandler.RespondWith(
            HttpStatusCode.BadRequest,
            $$"""{"error":"invalid_grant","error_description":"{{errorDescription}}"}"""));
        using CapturingLoggerProvider logs = new();
        using LoggerFactory loggerFactory = new([logs]);
        TokenEndpointClient client = CreateClient(httpClient, loggerFactory.CreateLogger<TokenEndpointClient>());

        await client.RefreshAsync(RefreshToken);

        CapturingLoggerProvider.LogEntry entry = logs.Entries.ShouldHaveSingleItem();
        entry.Message.ShouldEndWith($"Description: {new string('d', 200)}{expectedSuffix}");
    }

    [Fact]
    public async Task RefreshAsync_WhenTheErrorCodeIsLongerThanTheCap_LogsOnlyItsStart()
    {
        string error = new('e', 201);
        using HttpClient httpClient = CreateHttpClient(StubHttpMessageHandler.RespondWith(
            HttpStatusCode.BadRequest,
            $$"""{"error":"{{error}}","error_description":"Refused."}"""));
        using CapturingLoggerProvider logs = new();
        using LoggerFactory loggerFactory = new([logs]);
        TokenEndpointClient client = CreateClient(httpClient, loggerFactory.CreateLogger<TokenEndpointClient>());

        await client.RefreshAsync(RefreshToken);

        CapturingLoggerProvider.LogEntry entry = logs.Entries.ShouldHaveSingleItem();
        entry.Message.ShouldContain($"Error: {error[..200]}...");
        entry.Message.ShouldNotContain(error);
        entry.Message.ShouldContain("Refused.");
    }

    /// <summary>
    /// RFC 6749 allows only printable ASCII in these fields. A line break could pass for a separate log
    /// entry, and a bidi mark can make the line read as something else, so nothing outside that range
    /// reaches the log.
    /// </summary>
    [Theory]
    [InlineData("Refused.\\r\\nWarning: forged entry")]
    [InlineData("Refused.\\nWarning: forged entry")]
    [InlineData("Refused.\\u0000\\u001bWarning: forged entry")]
    [InlineData("Refused.\\u0085Warning: forged entry")]
    [InlineData("Refused.\\u2028Warning: forged entry")]
    [InlineData("Refused.\\u2029Warning: forged entry")]
    [InlineData("Refused.\\u202eWarning: forged entry")]
    [InlineData("Refused.\\u200bWarning: forged entry")]
    [InlineData("Odrzucono \\ud83d\\ude00 forged entry")]
    public async Task RefreshAsync_WhenTheReasonHoldsCharactersOutsidePrintableAscii_LogsOnlyPrintableAscii(
        string jsonEscapedDescription)
    {
        using HttpClient httpClient = CreateHttpClient(StubHttpMessageHandler.RespondWith(
            HttpStatusCode.BadRequest,
            $$"""{"error":"invalid_grant","error_description":"{{jsonEscapedDescription}}"}"""));
        using CapturingLoggerProvider logs = new();
        using LoggerFactory loggerFactory = new([logs]);
        TokenEndpointClient client = CreateClient(httpClient, loggerFactory.CreateLogger<TokenEndpointClient>());

        await client.RefreshAsync(RefreshToken);

        CapturingLoggerProvider.LogEntry entry = logs.Entries.ShouldHaveSingleItem();
        entry.Message.ShouldContain("forged entry");
        entry.Message.ShouldAllBe(character => character >= ' ' && character <= '~');
    }

    [Fact]
    public async Task RefreshAsync_WhenTheCapCutsASurrogatePair_LogsNoHalfOfIt()
    {
        string jsonEscapedDescription = new string('d', 199) + "\\ud83d\\ude00tail";
        using HttpClient httpClient = CreateHttpClient(StubHttpMessageHandler.RespondWith(
            HttpStatusCode.BadRequest,
            $$"""{"error":"invalid_grant","error_description":"{{jsonEscapedDescription}}"}"""));
        using CapturingLoggerProvider logs = new();
        using LoggerFactory loggerFactory = new([logs]);
        TokenEndpointClient client = CreateClient(httpClient, loggerFactory.CreateLogger<TokenEndpointClient>());

        await client.RefreshAsync(RefreshToken);

        CapturingLoggerProvider.LogEntry entry = logs.Entries.ShouldHaveSingleItem();
        entry.Message.ShouldEndWith($"Description: {new string('d', 199)}?...");
        entry.Message.ShouldAllBe(character => character >= ' ' && character <= '~');
    }

    /// <summary>
    /// Decoding the body as text throws on a charset .NET does not know, such as the common misspelling
    /// "utf8". The body is only read for the log, so it must never turn a refused refresh into an
    /// exception. A UTF-8 byte order mark must not hide the reason either.
    /// </summary>
    [Theory]
    [InlineData("utf8", false)]
    [InlineData("bogus", false)]
    [InlineData("utf-8", true)]
    [InlineData(null, true)]
    public async Task RefreshAsync_WhenTheRefusalHasAnUnusualEncoding_StillLogsTheReason(string? charset, bool withByteOrderMark)
    {
        byte[] json = """{"error":"invalid_grant","error_description":"Refused."}"""u8.ToArray();
        byte[] body = withByteOrderMark ? [.. System.Text.Encoding.UTF8.GetPreamble(), .. json] : json;
        using HttpClient httpClient = CreateHttpClient(StubHttpMessageHandler.RespondWith(
            HttpStatusCode.BadRequest,
            () => new ByteArrayContent(body)
            {
                Headers = { ContentType = new MediaTypeHeaderValue("application/json") { CharSet = charset } }
            }));
        using CapturingLoggerProvider logs = new();
        using LoggerFactory loggerFactory = new([logs]);
        TokenEndpointClient client = CreateClient(httpClient, loggerFactory.CreateLogger<TokenEndpointClient>());

        TokenResponse? response = await client.RefreshAsync(RefreshToken);

        response.ShouldBeNull();
        CapturingLoggerProvider.LogEntry entry = logs.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Message.ShouldContain("400");
        entry.Message.ShouldContain("invalid_grant");
        entry.Message.ShouldContain("Refused.");
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("bogus")]
    public async Task RefreshAsync_WhenANonOAuthRefusalHasAnUnknownCharset_LogsOneWarningWithTheStatusAlone(string charset)
    {
        using HttpClient httpClient = CreateHttpClient(StubHttpMessageHandler.RespondWith(
            HttpStatusCode.BadGateway,
            () => new StringContent("<html><body>502 Bad Gateway</body></html>")
            {
                Headers = { ContentType = new MediaTypeHeaderValue("text/html") { CharSet = charset } }
            }));
        using CapturingLoggerProvider logs = new();
        using LoggerFactory loggerFactory = new([logs]);
        TokenEndpointClient client = CreateClient(httpClient, loggerFactory.CreateLogger<TokenEndpointClient>());

        TokenResponse? response = await client.RefreshAsync(RefreshToken);

        response.ShouldBeNull();
        CapturingLoggerProvider.LogEntry entry = logs.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Message.ShouldBe("Refresh token grant failed with status 502.");
    }

    private static HttpClient CreateHttpClient(HttpMessageHandler transport) =>
        new(transport) { BaseAddress = new Uri(AuthBaseUrl) };

    private static TokenEndpointClient CreateClient(
        HttpClient httpClient,
        ILogger<TokenEndpointClient>? logger = null) => new(
        httpClient,
        Microsoft.Extensions.Options.Options.Create(new AuthSystemSettings
        {
            BaseUrl = AuthBaseUrl,
            Authority = "https://auth.lotro.test",
            ClientId = "lotrokoniecdev-web",
            CallbackPath = "/callback",
            SignedOutCallbackPath = "/signout-callback-oidc",
            Scopes = ["openid", "email", "profile"]
        }),
        logger ?? NullLogger<TokenEndpointClient>.Instance);
}
