using System.Diagnostics;
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
    private const string AuthBaseUrl = "https://auth.lotro.test/";
    private const string RefreshToken = "the-refresh-token";

    // Built rather than written out, so no secret scanner mistakes test data for a token.
    private static readonly string AccessToken = new('a', 40);

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    private static readonly Uri RevocationEndpoint = new("https://auth.lotro.test/connect/revoke");

    private static readonly byte[] InvalidUtf8InTheAccessToken =
        [.. """{"access_token":"a"""u8, 0xFF, .. """a","expires_in":3600}"""u8];

    [Fact]
    public async Task RefreshAsync_WhenTheAuthApiAnswersWithTokens_ReturnsThem()
    {
        using HttpClient httpClient = CreateHttpClient(StubHttpMessageHandler.RespondWith(
            HttpStatusCode.OK,
            $$"""{"access_token":"{{AccessToken}}","expires_in":3600}"""));
        TokenEndpointClient client = CreateClient(httpClient);

        TokenRefreshResult result = await client.RefreshAsync(RefreshToken);

        result.Tokens.ShouldNotBeNull().AccessToken.ShouldBe(AccessToken);
    }

    /// <summary>
    /// #974: a missing lifetime must stay apart from a zero one, so the refresh can log which it got.
    /// </summary>
    [Theory]
    [InlineData("""{"access_token":"x"}""", null)]
    [InlineData("""{"access_token":"x","expires_in":null}""", null)]
    [InlineData("""{"access_token":"x","expires_in":0}""", 0)]
    [InlineData("""{"access_token":"x","expires_in":-1}""", -1)]
    [InlineData("""{"access_token":"x","expires_in":3600}""", 3600)]
    [InlineData("""{"access_token":"x","expires_in":"3600"}""", 3600)]
    public async Task RefreshAsync_WhenTheAnswerSendsOrOmitsExpiresIn_ReturnsTheLifetimeAsSent(string body, int? expectedExpiresIn)
    {
        using HttpClient httpClient = CreateHttpClient(StubHttpMessageHandler.RespondWith(HttpStatusCode.OK, body));
        TokenEndpointClient client = CreateClient(httpClient);

        TokenRefreshResult result = await client.RefreshAsync(RefreshToken);

        result.Tokens.ShouldNotBeNull().ExpiresIn.ShouldBe(expectedExpiresIn);
    }

    /// <summary>
    /// #943: a charset .NET does not know, such as the common misspelling "utf8", must not stop the
    /// refresh, because the tokens are read from the bytes. A UTF-8 byte order mark must not stop it either.
    /// </summary>
    [Theory]
    [InlineData("utf8", false)]
    [InlineData("bogus", false)]
    [InlineData("utf8", true)]
    [InlineData("utf-8", true)]
    [InlineData(null, true)]
    public async Task RefreshAsync_WhenTheTokenAnswerHasAnUnusualEncoding_ReturnsTheTokens(string? charset, bool withByteOrderMark)
    {
        byte[] json = System.Text.Encoding.UTF8.GetBytes($$"""{"access_token":"{{AccessToken}}","expires_in":3600}""");
        byte[] body = withByteOrderMark ? [.. System.Text.Encoding.UTF8.GetPreamble(), .. json] : json;
        using HttpClient httpClient = CreateHttpClient(StubHttpMessageHandler.RespondWith(HttpStatusCode.OK, JsonBytes(body, charset)));
        TokenEndpointClient client = CreateClient(httpClient);

        TokenRefreshResult result = await client.RefreshAsync(RefreshToken);

        result.Tokens.ShouldNotBeNull().AccessToken.ShouldBe(AccessToken);
    }

    public static TheoryData<byte[], string?> NonUtf8TokenAnswers => new()
    {
        { InvalidUtf8InTheAccessToken, "utf-8" },
        { InvalidUtf8InTheAccessToken, "utf8" },
        { [.. System.Text.Encoding.Unicode.GetPreamble(), .. System.Text.Encoding.Unicode.GetBytes("""{"access_token":"a"}""")], "utf-16" },
        { System.Text.Encoding.Unicode.GetBytes("""{"access_token":"a"}"""), "bogus" }
    };

    /// <summary>
    /// #943: JSON between services is UTF-8 (RFC 8259 §8.1), so the answer is read as UTF-8 whatever
    /// charset its header names. A UTF-16 body, or a token with bytes that are not UTF-8, is a final
    /// failure (ADR-0067 decision 3). It never throws.
    /// </summary>
    [Theory]
    [MemberData(nameof(NonUtf8TokenAnswers))]
    public async Task RefreshAsync_WhenTheTokenAnswerIsNotUtf8_IsAFinalFailure(byte[] body, string? charset)
    {
        using HttpClient httpClient = CreateHttpClient(StubHttpMessageHandler.RespondWith(HttpStatusCode.OK, JsonBytes(body, charset)));
        TokenEndpointClient client = CreateClient(httpClient);

        TokenRefreshResult result = await client.RefreshAsync(RefreshToken);

        result.ShouldBe(TokenRefreshResult.FinalFailure);
    }

    /// <summary>
    /// A proxy can answer 200 with a body that carries no tokens. That is a final failure, never an exception:
    /// the whole body arrived, so a retry would get the same answer (ADR-0067 decision 3).
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("<html><body>200 OK</body></html>")]
    [InlineData("""{"access_token":5}""")]
    [InlineData("""{"access_token":"a""")]
    [InlineData("""{"access_token":"a","expires_in":3600.5}""")]
    [InlineData("""{"access_token":"a","expires_in":2147483648}""")]
    [InlineData("""{"access_token":"a","expires_in":"soon"}""")]
    [InlineData("""{"access_token":"a","expires_in":true}""")]
    public async Task RefreshAsync_WhenTheTokenAnswerIsNotATokenObject_IsAFinalFailure(string body)
    {
        using HttpClient httpClient = CreateHttpClient(StubHttpMessageHandler.RespondWith(HttpStatusCode.OK, body));
        TokenEndpointClient client = CreateClient(httpClient);

        TokenRefreshResult result = await client.RefreshAsync(RefreshToken);

        result.ShouldBe(TokenRefreshResult.FinalFailure);
    }

    /// <summary>
    /// #899: the primary handler follows no redirect, so a redirect reaches this client as it is. It has
    /// to count as a final failure, the same as any other refusal that is not a 429 or a 5xx.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.SeeOther)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task RefreshAsync_WhenTheAuthApiAnswersWithARedirect_IsAFinalFailure(HttpStatusCode statusCode)
    {
        using HttpClient httpClient = CreateHttpClient(StubHttpMessageHandler.RespondWithHeaders(
            statusCode,
            new Dictionary<string, string> { ["Location"] = "https://attacker.example/connect/token" }));
        TokenEndpointClient client = CreateClient(httpClient);

        TokenRefreshResult result = await client.RefreshAsync(RefreshToken);

        result.ShouldBe(TokenRefreshResult.FinalFailure);
    }

    /// <summary>
    /// #923: when the client's time limit runs out on a stuck sign-in server, the refresh is a temporary
    /// failure (#1062) instead of an exception. The registration's limit is pinned in
    /// <c>AuthenticationDependencyInjectionExtensionsTests</c>.
    /// </summary>
    [Fact]
    public async Task RefreshAsync_WhenTheAuthApiNeverAnswers_IsATemporaryFailureOnceTheClientTimesOut()
    {
        using HttpClient httpClient = new(new NeverAnsweringHttpMessageHandler())
        {
            BaseAddress = new Uri(AuthBaseUrl),
            Timeout = TimeSpan.FromMilliseconds(50)
        };
        TokenEndpointClient client = CreateClient(httpClient);

        TokenRefreshResult result = await client.RefreshAsync(RefreshToken).WaitAsync(TestTimeout);

        result.ShouldBe(TokenRefreshResult.TemporaryFailure);
    }

    /// <summary>
    /// #1141: a stuck sign-in server is a fault someone should look at, so the timeout stays a warning.
    /// </summary>
    [Fact]
    public async Task RefreshAsync_WhenTheClientTimesOut_LogsOneWarning()
    {
        using HttpClient httpClient = new(new NeverAnsweringHttpMessageHandler())
        {
            BaseAddress = new Uri(AuthBaseUrl),
            Timeout = TimeSpan.FromMilliseconds(50)
        };
        using CapturingLoggerProvider logs = new();
        using LoggerFactory loggerFactory = new([logs]);
        TokenEndpointClient client = CreateClient(httpClient, loggerFactory.CreateLogger<TokenEndpointClient>());
        using CancellationTokenSource requestAborted = new();

        await client.RefreshAsync(RefreshToken, requestAborted.Token).WaitAsync(TestTimeout);

        CapturingLoggerProvider.LogEntry entry = logs.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Message.ShouldBe("Refresh token grant threw an exception.");
    }

    /// <summary>
    /// #1141: the browser left during the renewal, for example with a click on another link. The cookie
    /// check keeps the stored token for the browser's next request, so the failure is temporary.
    /// </summary>
    [Fact]
    public async Task RefreshAsync_WhenTheBrowserLeavesDuringTheRenewal_IsATemporaryFailure()
    {
        NeverAnsweringHttpMessageHandler transport = new();
        using HttpClient httpClient = CreateHttpClient(transport);
        TokenEndpointClient client = CreateClient(httpClient);
        using CancellationTokenSource requestAborted = new();

        Task<TokenRefreshResult> refresh = client.RefreshAsync(RefreshToken, requestAborted.Token);
        await transport.RequestStarted.Task.WaitAsync(TestTimeout);
        await requestAborted.CancelAsync();
        TokenRefreshResult result = await refresh.WaitAsync(TestTimeout);

        result.ShouldBe(TokenRefreshResult.TemporaryFailure);
    }

    /// <summary>
    /// #1141: a browser that leaves is a normal user action, so it is no warning (#977).
    /// </summary>
    [Fact]
    public async Task RefreshAsync_WhenTheBrowserLeavesDuringTheRenewal_LogsOneInformationLine()
    {
        NeverAnsweringHttpMessageHandler transport = new();
        using HttpClient httpClient = CreateHttpClient(transport);
        using CapturingLoggerProvider logs = new();
        using LoggerFactory loggerFactory = new([logs]);
        TokenEndpointClient client = CreateClient(httpClient, loggerFactory.CreateLogger<TokenEndpointClient>());
        using CancellationTokenSource requestAborted = new();

        Task<TokenRefreshResult> refresh = client.RefreshAsync(RefreshToken, requestAborted.Token);
        await transport.RequestStarted.Task.WaitAsync(TestTimeout);
        await requestAborted.CancelAsync();
        await refresh.WaitAsync(TestTimeout);

        CapturingLoggerProvider.LogEntry entry = logs.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Information);
        entry.Message.ShouldBe("Refresh token grant was cancelled because the request was aborted.");
    }

    /// <summary>
    /// #1062: the owner's list of failures that go away on their own: a busy or restarting sign-in server,
    /// and the per-address limit that many users behind one address share.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData((HttpStatusCode)599)]
    public async Task RefreshAsync_WhenTheAuthApiFailsForAMoment_IsATemporaryFailure(HttpStatusCode statusCode)
    {
        using HttpClient httpClient = CreateHttpClient(StubHttpMessageHandler.RespondWith(
            statusCode,
            """{"error":"temporarily_unavailable"}"""));
        TokenEndpointClient client = CreateClient(httpClient);

        TokenRefreshResult result = await client.RefreshAsync(RefreshToken);

        result.ShouldBe(TokenRefreshResult.TemporaryFailure);
    }

    /// <summary>
    /// #1062: every other refusal is final, <c>invalid_grant</c> first of all. A 408 is not in the owner's list.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData((HttpStatusCode)428)]
    [InlineData((HttpStatusCode)430)]
    [InlineData((HttpStatusCode)499)]
    [InlineData((HttpStatusCode)600)]
    public async Task RefreshAsync_WhenTheAuthApiRefusesForGood_IsAFinalFailure(HttpStatusCode statusCode)
    {
        using HttpClient httpClient = CreateHttpClient(StubHttpMessageHandler.RespondWith(
            statusCode,
            """{"error":"invalid_grant"}"""));
        TokenEndpointClient client = CreateClient(httpClient);

        TokenRefreshResult result = await client.RefreshAsync(RefreshToken);

        result.ShouldBe(TokenRefreshResult.FinalFailure);
    }

    public static TheoryData<Exception> RefreshTransportFailures() => new()
    {
        new HttpRequestException("Connection refused."),
        new TaskCanceledException("The request was cancelled.")
    };

    /// <summary>
    /// #1062: a sign-in server that cannot be reached, for example while it restarts during a deploy.
    /// </summary>
    [Theory]
    [MemberData(nameof(RefreshTransportFailures))]
    public async Task RefreshAsync_WhenTheAuthApiCannotBeReached_IsATemporaryFailure(Exception failure)
    {
        using HttpClient httpClient = CreateHttpClient(StubHttpMessageHandler.Throw(failure));
        TokenEndpointClient client = CreateClient(httpClient);

        TokenRefreshResult result = await client.RefreshAsync(RefreshToken);

        result.ShouldBe(TokenRefreshResult.TemporaryFailure);
    }

    /// <summary>
    /// ADR-0067 decision 3: a body cut off on the way fails as a network error, so it is temporary. Only a
    /// whole body that cannot be read is final.
    /// </summary>
    [Fact]
    public async Task RefreshAsync_WhenTheAnswerIsCutOffOnTheWay_IsATemporaryFailure()
    {
        using HttpClient httpClient = CreateHttpClient(StubHttpMessageHandler.RespondWith(
            HttpStatusCode.OK,
            () => new CutOffContent()));
        TokenEndpointClient client = CreateClient(httpClient);

        TokenRefreshResult result = await client.RefreshAsync(RefreshToken);

        result.ShouldBe(TokenRefreshResult.TemporaryFailure);
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

        TokenRefreshResult result = await client.RefreshAsync(RefreshToken);

        result.ShouldBe(TokenRefreshResult.FinalFailure);
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

        TokenRefreshResult result = await client.RefreshAsync(RefreshToken);

        result.ShouldBe(TokenRefreshResult.FinalFailure);
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

        TokenRefreshResult result = await client.RefreshAsync(RefreshToken);

        result.ShouldBe(TokenRefreshResult.FinalFailure);
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
        using HttpClient httpClient = CreateHttpClient(StubHttpMessageHandler.RespondWith(HttpStatusCode.BadRequest, JsonBytes(body, charset)));
        using CapturingLoggerProvider logs = new();
        using LoggerFactory loggerFactory = new([logs]);
        TokenEndpointClient client = CreateClient(httpClient, loggerFactory.CreateLogger<TokenEndpointClient>());

        TokenRefreshResult result = await client.RefreshAsync(RefreshToken);

        result.ShouldBe(TokenRefreshResult.FinalFailure);
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

        TokenRefreshResult result = await client.RefreshAsync(RefreshToken);

        result.ShouldBe(TokenRefreshResult.TemporaryFailure);
        CapturingLoggerProvider.LogEntry entry = logs.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Message.ShouldBe("Refresh token grant failed with status 502.");
    }

    /// <summary>
    /// #964: the sign-out's own revoke, as RFC 7009 describes it for a public client: the token, a hint
    /// of its kind and the client id, sent to the endpoint the discovery document named.
    /// </summary>
    [Fact]
    public async Task RevokeRefreshTokenAsync_Always_SendsTheTokenItsKindAndTheClientIdToTheGivenEndpoint()
    {
        StubHttpMessageHandler transport = StubHttpMessageHandler.RespondWith(HttpStatusCode.OK, string.Empty);
        using HttpClient httpClient = CreateHttpClient(transport);
        TokenEndpointClient client = CreateClient(httpClient);

        await client.RevokeRefreshTokenAsync(RevocationEndpoint, RefreshToken);

        transport.LastRequest.ShouldNotBeNull().Method.ShouldBe(HttpMethod.Post);
        transport.LastRequest.RequestUri.ShouldBe(RevocationEndpoint);
        transport.LastRequestBody.ShouldBe(
            $"token={RefreshToken}&token_type_hint=refresh_token&client_id=lotrokoniecdev-web");
    }

    [Fact]
    public async Task RevokeRefreshTokenAsync_WhenTheAuthApiRevokes_LogsNothing()
    {
        using HttpClient httpClient = CreateHttpClient(StubHttpMessageHandler.RespondWith(HttpStatusCode.OK, string.Empty));
        using CapturingLoggerProvider logs = new();
        using LoggerFactory loggerFactory = new([logs]);
        TokenEndpointClient client = CreateClient(httpClient, loggerFactory.CreateLogger<TokenEndpointClient>());

        await client.RevokeRefreshTokenAsync(RevocationEndpoint, RefreshToken);

        logs.Entries.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "unsupported_token_type", "The specified token cannot be revoked.")]
    [InlineData(HttpStatusCode.Unauthorized, "invalid_client", "The specified client_id is invalid.")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "temporarily_unavailable", "Try again later.")]
    public async Task RevokeRefreshTokenAsync_WhenTheAuthApiRefusesWithTheOAuthErrorBody_LogsOneWarningWithTheStatusAndTheReason(
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

        await client.RevokeRefreshTokenAsync(RevocationEndpoint, RefreshToken);

        CapturingLoggerProvider.LogEntry entry = logs.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Message.ShouldBe(
            $"Refresh token revocation failed with status {(int)statusCode}. Error: {error}. Description: {errorDescription}");
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway, "<html><body>502 Bad Gateway</body></html>")]
    [InlineData(HttpStatusCode.BadRequest, "")]
    [InlineData(HttpStatusCode.BadRequest, """{"error":"","error_description":"no error code"}""")]
    [InlineData(HttpStatusCode.InternalServerError, """{"title":"Internal Server Error","status":500}""")]
    public async Task RevokeRefreshTokenAsync_WhenTheRefusalBodyIsNotTheOAuthShape_LogsOneWarningWithTheStatusAlone(
        HttpStatusCode statusCode,
        string body)
    {
        using HttpClient httpClient = CreateHttpClient(StubHttpMessageHandler.RespondWith(statusCode, body));
        using CapturingLoggerProvider logs = new();
        using LoggerFactory loggerFactory = new([logs]);
        TokenEndpointClient client = CreateClient(httpClient, loggerFactory.CreateLogger<TokenEndpointClient>());

        await client.RevokeRefreshTokenAsync(RevocationEndpoint, RefreshToken);

        CapturingLoggerProvider.LogEntry entry = logs.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Message.ShouldBe($"Refresh token revocation failed with status {(int)statusCode}.");
    }

    /// <summary>
    /// #899: the primary handler follows no redirect, so a redirect is a failed revoke like any other.
    /// </summary>
    [Fact]
    public async Task RevokeRefreshTokenAsync_WhenTheAuthApiAnswersWithARedirect_LogsOneWarningWithTheStatus()
    {
        using HttpClient httpClient = CreateHttpClient(StubHttpMessageHandler.RespondWithHeaders(
            HttpStatusCode.TemporaryRedirect,
            new Dictionary<string, string> { ["Location"] = "https://attacker.example/connect/revoke" }));
        using CapturingLoggerProvider logs = new();
        using LoggerFactory loggerFactory = new([logs]);
        TokenEndpointClient client = CreateClient(httpClient, loggerFactory.CreateLogger<TokenEndpointClient>());

        await client.RevokeRefreshTokenAsync(RevocationEndpoint, RefreshToken);

        CapturingLoggerProvider.LogEntry entry = logs.Entries.ShouldHaveSingleItem();
        entry.Message.ShouldBe("Refresh token revocation failed with status 307.");
    }

    public static TheoryData<Exception> TransportFailures() => new()
    {
        new HttpRequestException("Connection refused."),
        new TaskCanceledException("The revoke timed out."),
        new OperationCanceledException("The sign-out's time limit ran out.")
    };

    /// <summary>
    /// A revoke that cannot reach the auth API must never turn the sign-out into an error page.
    /// </summary>
    [Theory]
    [MemberData(nameof(TransportFailures))]
    public async Task RevokeRefreshTokenAsync_WhenTheAuthApiCannotBeReached_LogsOneWarningAndDoesNotThrow(Exception failure)
    {
        using HttpClient httpClient = CreateHttpClient(StubHttpMessageHandler.Throw(failure));
        using CapturingLoggerProvider logs = new();
        using LoggerFactory loggerFactory = new([logs]);
        TokenEndpointClient client = CreateClient(httpClient, loggerFactory.CreateLogger<TokenEndpointClient>());

        await client.RevokeRefreshTokenAsync(RevocationEndpoint, RefreshToken);

        CapturingLoggerProvider.LogEntry entry = logs.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Message.ShouldBe("Refresh token revocation threw an exception.");
    }

    [Theory]
    [InlineData($$"""{"error":"invalid_request","error_description":"Refused.","token":"{{RefreshToken}}"}""")]
    [InlineData("token=" + RefreshToken + "&token_type_hint=refresh_token")]
    public async Task RevokeRefreshTokenAsync_WhenTheRefusalBodyRepeatsTheRefreshToken_NeverLogsIt(string body)
    {
        using HttpClient httpClient = CreateHttpClient(StubHttpMessageHandler.RespondWith(HttpStatusCode.BadRequest, body));
        using CapturingLoggerProvider logs = new();
        using LoggerFactory loggerFactory = new([logs]);
        TokenEndpointClient client = CreateClient(httpClient, loggerFactory.CreateLogger<TokenEndpointClient>());

        await client.RevokeRefreshTokenAsync(RevocationEndpoint, RefreshToken);

        CapturingLoggerProvider.LogEntry entry = logs.Entries.ShouldHaveSingleItem();
        entry.Message.ShouldNotContain(RefreshToken);
    }

    private static Func<HttpContent> JsonBytes(byte[] body, string? charset) =>
        () => new ByteArrayContent(body)
        {
            Headers = { ContentType = new MediaTypeHeaderValue("application/json") { CharSet = charset } }
        };

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

    /// <summary>A sign-in server that took the connection but never answers. Only cancellation ends the wait.</summary>
    private sealed class NeverAnsweringHttpMessageHandler : HttpMessageHandler
    {
        public TaskCompletionSource RequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new UnreachableException();
        }
    }

    /// <summary>A success answer whose connection closes before the whole body has arrived.</summary>
    private sealed class CutOffContent : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync("""{"access_token":"a"""u8.ToArray());
            throw new IOException("The connection closed before the whole body arrived.");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
