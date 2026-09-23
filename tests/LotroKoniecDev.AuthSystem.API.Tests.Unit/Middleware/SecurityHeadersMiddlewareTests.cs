using LotroKoniecDev.AuthSystem.API.Middleware;
using LotroKoniecDev.AuthSystem.API.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Middleware;

public sealed class SecurityHeadersMiddlewareTests
{
    private const string Nonce = "r4nd0m-n0nce_value";
    private const string FrontendOrigin = "https://app.lotro-translator.pl";

    [Theory]
    [InlineData("https://app.lotro-translator.pl/callback", "https://app.lotro-translator.pl")]
    [InlineData("https://app.lotro-translator.pl/", "https://app.lotro-translator.pl")]
    [InlineData("https://localhost:7017/callback", "https://localhost:7017")]
    [InlineData("http://localhost:5000/callback?x=1#frag", "http://localhost:5000")]
    public void FrontendOrigins_ShouldCutEachUriDownToItsOrigin(string redirectUri, string expectedOrigin)
    {
        // Arrange
        WebClientSettings webClient = new() { RedirectUris = [redirectUri] };

        // Act
        IReadOnlyList<string> origins = SecurityHeadersMiddleware.FrontendOrigins(webClient);

        // Assert
        origins.ShouldBe([expectedOrigin]);
    }

    [Fact]
    public void FrontendOrigins_ShouldListOneOriginOnce_WhenTheRedirectAndPostLogoutUrisShareIt()
    {
        // Arrange: the shape every environment has, a callback path and the app root on one origin
        WebClientSettings webClient = new()
        {
            RedirectUris = [FrontendOrigin + "/callback"],
            PostLogoutRedirectUris = [FrontendOrigin, "HTTPS://APP.LOTRO-TRANSLATOR.PL/"]
        };

        // Act
        IReadOnlyList<string> origins = SecurityHeadersMiddleware.FrontendOrigins(webClient);

        // Assert
        origins.ShouldBe([FrontendOrigin]);
    }

    [Fact]
    public void FrontendOrigins_ShouldListEveryDistinctOrigin()
    {
        // Arrange
        WebClientSettings webClient = new()
        {
            RedirectUris = [FrontendOrigin + "/callback", "https://staging.lotro-translator.pl/callback"],
            PostLogoutRedirectUris = ["https://localhost:7017"]
        };

        // Act
        IReadOnlyList<string> origins = SecurityHeadersMiddleware.FrontendOrigins(webClient);

        // Assert
        origins.ShouldBe([FrontendOrigin, "https://staging.lotro-translator.pl", "https://localhost:7017"]);
    }

    /// <summary>
    /// On Unix a bare path parses as an absolute <c>file://</c> URI, so the scheme check is what keeps
    /// it out of the policy (the same trap <c>FrontendUrl</c> documents).
    /// </summary>
    [Theory]
    [InlineData("/callback")]
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,hi")]
    [InlineData("ftp://app.lotro-translator.pl/callback")]
    public void FrontendOrigins_ShouldSkipAValueThatIsNotAnAbsoluteHttpUrl(string redirectUri)
    {
        // Arrange
        WebClientSettings webClient = new() { RedirectUris = [redirectUri] };

        // Act
        IReadOnlyList<string> origins = SecurityHeadersMiddleware.FrontendOrigins(webClient);

        // Assert
        origins.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("default-src 'self'")]
    [InlineData("base-uri 'self'")]
    [InlineData("object-src 'none'")]
    [InlineData("frame-ancestors 'none'")]
    [InlineData("font-src 'self'")]
    public void BuildContentSecurityPolicy_ShouldContainTheLockedDownBaseline(string expectedDirective)
    {
        // Act
        string policy = SecurityHeadersMiddleware.BuildContentSecurityPolicy(Nonce, [FrontendOrigin]);

        // Assert
        policy.Split("; ").ShouldContain(expectedDirective);
    }

    [Fact]
    public void BuildContentSecurityPolicy_ShouldAdmitNoInlineScriptAtAll()
    {
        // Act
        string policy = SecurityHeadersMiddleware.BuildContentSecurityPolicy(Nonce, [FrontendOrigin]);

        // Assert: the one script the pages need is a file, so script-src carries no nonce either
        policy.Split("; ").ShouldContain("script-src 'self'");
        policy.ShouldNotContain("unsafe-inline");
        policy.ShouldNotContain("unsafe-eval");
    }

    [Fact]
    public void BuildContentSecurityPolicy_ShouldAdmitInlineStyleOnlyByTheRequestsNonce()
    {
        // Act
        string policy = SecurityHeadersMiddleware.BuildContentSecurityPolicy(Nonce, [FrontendOrigin]);

        // Assert
        policy.Split("; ").ShouldContain($"style-src 'self' 'nonce-{Nonce}'");
    }

    [Fact]
    public void BuildContentSecurityPolicy_ShouldLetAFormEndOnlyOnThisOriginOrTheFrontend()
    {
        // Act
        string policy = SecurityHeadersMiddleware.BuildContentSecurityPolicy(
            Nonce, [FrontendOrigin, "https://localhost:7017"]);

        // Assert
        policy.Split("; ").ShouldContain($"form-action 'self' {FrontendOrigin} https://localhost:7017");
    }

    [Fact]
    public void BuildContentSecurityPolicy_ShouldKeepFormsOnThisOrigin_WhenNoFrontendIsConfigured()
    {
        // Act
        string policy = SecurityHeadersMiddleware.BuildContentSecurityPolicy(Nonce, []);

        // Assert
        policy.Split("; ").ShouldContain("form-action 'self'");
    }

    [Fact]
    public async Task InvokeAsync_ShouldStampEveryHeader_WhenTheResponseStarts()
    {
        // Arrange
        RecordingResponseFeature responseFeature = new();
        DefaultHttpContext context = BuildContext(responseFeature);
        SecurityHeadersMiddleware middleware = new(_ => Task.CompletedTask, ConfiguredWebClient());

        // Act
        await middleware.InvokeAsync(context);
        await responseFeature.FireOnStartingAsync();

        // Assert
        IHeaderDictionary headers = responseFeature.Headers;
        string nonce = CspNonce.Get(context).ShouldNotBeNull();
        headers.ContentSecurityPolicy.ToString()
            .ShouldBe(SecurityHeadersMiddleware.BuildContentSecurityPolicy(nonce, [FrontendOrigin]));
        headers.XContentTypeOptions.ToString().ShouldBe("nosniff");
        headers["Referrer-Policy"].ToString().ShouldBe("no-referrer");
        headers.XFrameOptions.ToString().ShouldBe("DENY");
    }

    [Fact]
    public async Task InvokeAsync_ShouldReplaceTheFrameHeaderAntiforgeryAdds()
    {
        // Arrange: antiforgery writes SAMEORIGIN while a page renders its form, before the response starts
        RecordingResponseFeature responseFeature = new();
        DefaultHttpContext context = BuildContext(responseFeature);
        SecurityHeadersMiddleware middleware = new(
            httpContext =>
            {
                httpContext.Response.Headers.XFrameOptions = "SAMEORIGIN";
                return Task.CompletedTask;
            },
            ConfiguredWebClient());

        // Act
        await middleware.InvokeAsync(context);
        await responseFeature.FireOnStartingAsync();

        // Assert
        responseFeature.Headers.XFrameOptions.ToString().ShouldBe("DENY");
    }

    [Fact]
    public async Task InvokeAsync_ShouldIssueTheNonceBeforeThePageRenders()
    {
        // Arrange
        RecordingResponseFeature responseFeature = new();
        DefaultHttpContext context = BuildContext(responseFeature);
        string? nonceSeenByThePage = null;
        SecurityHeadersMiddleware middleware = new(
            httpContext =>
            {
                nonceSeenByThePage = CspNonce.Get(httpContext);
                return Task.CompletedTask;
            },
            ConfiguredWebClient());

        // Act
        await middleware.InvokeAsync(context);
        await responseFeature.FireOnStartingAsync();

        // Assert
        nonceSeenByThePage.ShouldNotBeNullOrWhiteSpace();
        responseFeature.Headers.ContentSecurityPolicy.ToString().ShouldContain($"'nonce-{nonceSeenByThePage}'");
    }

    [Fact]
    public async Task InvokeAsync_ShouldIssueADifferentBase64UrlNonceForEachRequest()
    {
        // Arrange
        SecurityHeadersMiddleware middleware = new(_ => Task.CompletedTask, ConfiguredWebClient());
        DefaultHttpContext first = BuildContext(new RecordingResponseFeature());
        DefaultHttpContext second = BuildContext(new RecordingResponseFeature());

        // Act
        await middleware.InvokeAsync(first);
        await middleware.InvokeAsync(second);

        // Assert: base64url, because a '+' would be HTML-encoded in the attribute and stop matching
        string firstNonce = CspNonce.Get(first).ShouldNotBeNull();
        string secondNonce = CspNonce.Get(second).ShouldNotBeNull();
        firstNonce.ShouldNotBe(secondNonce);
        firstNonce.ShouldMatch("^[A-Za-z0-9_-]{43}$");
    }

    [Fact]
    public void CspNonce_ShouldBeNull_WhenTheMiddlewareDidNotRun()
    {
        // Arrange
        DefaultHttpContext context = new();

        // Act
        string? nonce = CspNonce.Get(context);

        // Assert
        nonce.ShouldBeNull();
    }

    private static IOptions<OpenIddictSettings> ConfiguredWebClient() =>
        Microsoft.Extensions.Options.Options.Create(new OpenIddictSettings
        {
            Issuer = "https://auth.lotro-translator.pl",
            WebClient = new WebClientSettings
            {
                RedirectUris = [FrontendOrigin + "/callback"],
                PostLogoutRedirectUris = [FrontendOrigin]
            }
        });

    private static DefaultHttpContext BuildContext(IHttpResponseFeature responseFeature)
    {
        FeatureCollection features = new();
        features.Set<IHttpRequestFeature>(new HttpRequestFeature());
        features.Set(responseFeature);
        return new DefaultHttpContext(features);
    }

    private sealed class RecordingResponseFeature : IHttpResponseFeature
    {
        private readonly List<(Func<object, Task> Callback, object State)> _onStarting = [];

        public int StatusCode { get; set; } = StatusCodes.Status200OK;
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = Stream.Null;
        public bool HasStarted { get; private set; }

        public void OnStarting(Func<object, Task> callback, object state) => _onStarting.Add((callback, state));

        public void OnCompleted(Func<object, Task> callback, object state)
        {
        }

        public async Task FireOnStartingAsync()
        {
            HasStarted = true;
            foreach ((Func<object, Task> callback, object state) in _onStarting)
            {
                await callback(state);
            }
        }
    }
}
