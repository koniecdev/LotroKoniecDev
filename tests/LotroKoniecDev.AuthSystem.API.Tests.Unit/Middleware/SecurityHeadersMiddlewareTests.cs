using LotroKoniecDev.AuthSystem.API.Middleware;
using LotroKoniecDev.AuthSystem.API.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Middleware;

public sealed class SecurityHeadersMiddlewareTests
{
    private const string FrontendOrigin = "https://app.lotro-translator.pl";

    [Theory]
    [InlineData("https://app.lotro-translator.pl/callback", "https://app.lotro-translator.pl")]
    [InlineData("https://app.lotro-translator.pl/", "https://app.lotro-translator.pl")]
    [InlineData("https://localhost:7017/callback", "https://localhost:7017")]
    [InlineData("http://localhost:5000/callback?x=1#frag", "http://localhost:5000")]
    [InlineData("https://user:secret@app.lotro-translator.pl:8443/callback", "https://app.lotro-translator.pl:8443")]
    [InlineData("https://bücher.example:8443/callback", "https://xn--bcher-kva.example:8443")]
    [InlineData("https://ŁÓDŹ.example/callback", "https://xn--d-uga0v4h.example")]
    [InlineData("http://[::1]:5000/callback", "http://[::1]:5000")]
    [InlineData("https://127.0.0.1/callback", "https://127.0.0.1")]
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
        string policy = SecurityHeadersMiddleware.BuildContentSecurityPolicy([FrontendOrigin]);

        // Assert
        policy.Split("; ").ShouldContain(expectedDirective);
    }

    [Fact]
    public void BuildContentSecurityPolicy_ShouldAdmitNoInlineScriptAtAll()
    {
        // Act
        string policy = SecurityHeadersMiddleware.BuildContentSecurityPolicy([FrontendOrigin]);

        // Assert: the scripts the pages need are files
        policy.Split("; ").ShouldContain("script-src 'self'");
        policy.ShouldNotContain("unsafe-inline");
        policy.ShouldNotContain("unsafe-eval");
    }

    [Fact]
    public void BuildContentSecurityPolicy_ShouldAdmitNoInlineStyleAtAll()
    {
        // Act
        string policy = SecurityHeadersMiddleware.BuildContentSecurityPolicy([FrontendOrigin]);

        // Assert: the pages' styles are files under wwwroot (#833)
        policy.Split("; ").ShouldContain("style-src 'self'");
        policy.ShouldNotContain("nonce-");
    }

    [Fact]
    public void BuildContentSecurityPolicy_ShouldLetAFormEndOnlyOnThisOriginOrTheFrontend()
    {
        // Act
        string policy = SecurityHeadersMiddleware.BuildContentSecurityPolicy(
            [FrontendOrigin, "https://localhost:7017"]);

        // Assert
        policy.Split("; ").ShouldContain($"form-action 'self' {FrontendOrigin} https://localhost:7017");
    }

    [Fact]
    public void BuildContentSecurityPolicy_ShouldKeepFormsOnThisOrigin_WhenNoFrontendIsConfigured()
    {
        // Act
        string policy = SecurityHeadersMiddleware.BuildContentSecurityPolicy([]);

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
        headers.ContentSecurityPolicy.ToString().ShouldBe(
            "default-src 'self'; base-uri 'self'; object-src 'none'; frame-ancestors 'none'; script-src 'self'; "
            + $"style-src 'self'; font-src 'self'; form-action 'self' {FrontendOrigin}");
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
    public async Task InvokeAsync_ShouldStillStampTheHeaders_WhenTheResponseWasClearedForAnErrorPage()
    {
        // Arrange: the exception handler clears the response before it writes the error page
        RecordingResponseFeature responseFeature = new();
        DefaultHttpContext context = BuildContext(responseFeature);
        SecurityHeadersMiddleware middleware = new(
            httpContext =>
            {
                httpContext.Response.Headers.XFrameOptions = "SAMEORIGIN";
                httpContext.Response.Clear();
                httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
                return Task.CompletedTask;
            },
            ConfiguredWebClient());

        // Act
        await middleware.InvokeAsync(context);
        await responseFeature.FireOnStartingAsync();

        // Assert
        responseFeature.Headers.XFrameOptions.ToString().ShouldBe("DENY");
        responseFeature.Headers.ContentSecurityPolicy.ToString().ShouldContain("frame-ancestors 'none'");
    }

    [Fact]
    public async Task InvokeAsync_ShouldSendTheSamePolicyOnEveryRequest()
    {
        // Arrange: the policy holds no per-request value since the pages lost their inline styles (#833)
        SecurityHeadersMiddleware middleware = new(_ => Task.CompletedTask, ConfiguredWebClient());
        RecordingResponseFeature firstResponse = new();
        RecordingResponseFeature secondResponse = new();

        // Act
        await middleware.InvokeAsync(BuildContext(firstResponse));
        await firstResponse.FireOnStartingAsync();
        await middleware.InvokeAsync(BuildContext(secondResponse));
        await secondResponse.FireOnStartingAsync();

        // Assert
        string firstPolicy = firstResponse.Headers.ContentSecurityPolicy.ToString();
        firstPolicy.ShouldNotBeNullOrWhiteSpace();
        secondResponse.Headers.ContentSecurityPolicy.ToString().ShouldBe(firstPolicy);
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
        features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(Stream.Null));
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
