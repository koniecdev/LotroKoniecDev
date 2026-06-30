using LotroKoniecDev.Frontend.Infrastructure.Security;
using LotroKoniecDev.Frontend.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.Security;

public sealed class SecurityHeadersMiddlewareTests
{
    private const string AuthAuthority = "https://localhost:5003";

    [Theory]
    [InlineData("https://localhost:5003", "https://localhost:5003")]
    [InlineData("https://localhost:5003/", "https://localhost:5003")]
    [InlineData("https://auth.lotro-translator.pl", "https://auth.lotro-translator.pl")]
    [InlineData("https://auth.example.com:8443/connect/authorize", "https://auth.example.com:8443")]
    public void AuthOrigin_ReturnsSchemeHostAndPortWithoutPathOrTrailingSlash(string authority, string expected)
    {
        string result = SecurityHeadersMiddleware.AuthOrigin(authority);

        result.ShouldBe(expected);
    }

    [Theory]
    [InlineData("default-src 'self'")]
    [InlineData("base-uri 'self'")]
    [InlineData("object-src 'none'")]
    [InlineData("frame-ancestors 'none'")]
    [InlineData("img-src 'self' data:")]
    [InlineData("script-src 'self'")]
    public void BuildContentSecurityPolicy_ContainsTheLockedDownBaselineDirectives(string expectedDirective)
    {
        string policy = SecurityHeadersMiddleware.BuildContentSecurityPolicy(AuthAuthority);

        policy.ShouldContain(expectedDirective);
    }

    [Fact]
    public void BuildContentSecurityPolicy_AllowsTheAuthOriginForConnectAndFormAction()
    {
        string policy = SecurityHeadersMiddleware.BuildContentSecurityPolicy("https://localhost:5003");

        policy.ShouldContain("connect-src 'self' https://localhost:5003");
        policy.ShouldContain("form-action 'self' https://localhost:5003");
    }

    [Fact]
    public void BuildContentSecurityPolicy_AllowsTheGoogleFontsOriginsTheLayoutDependsOn()
    {
        string policy = SecurityHeadersMiddleware.BuildContentSecurityPolicy(AuthAuthority);

        policy.ShouldContain("style-src 'self' 'unsafe-inline' https://fonts.googleapis.com");
        policy.ShouldContain("font-src 'self' https://fonts.gstatic.com");
    }

    [Fact]
    public void BuildContentSecurityPolicy_DoesNotWeakenScriptSrcWithUnsafeInlineOrEval()
    {
        string policy = SecurityHeadersMiddleware.BuildContentSecurityPolicy(AuthAuthority);

        policy.ShouldNotContain("script-src 'self' 'unsafe-inline'");
        policy.ShouldNotContain("'unsafe-eval'");
    }

    [Fact]
    public void BuildHeaders_SetsEveryRequiredSecurityHeader()
    {
        IReadOnlyDictionary<string, string> headers = SecurityHeadersMiddleware.BuildHeaders(AuthAuthority);

        headers["X-Content-Type-Options"].ShouldBe("nosniff");
        headers["Referrer-Policy"].ShouldBe("no-referrer");
        headers["X-Frame-Options"].ShouldBe("DENY");
        headers["Content-Security-Policy"].ShouldBe(SecurityHeadersMiddleware.BuildContentSecurityPolicy(AuthAuthority));
    }

    [Fact]
    public async Task InvokeAsync_StampsEverySecurityHeaderOnTheResponse()
    {
        RecordingResponseFeature responseFeature = new();
        DefaultHttpContext context = BuildContext(responseFeature);
        SecurityHeadersMiddleware middleware = new(_ => Task.CompletedTask, Microsoft.Extensions.Options.Options.Create(ValidAuthSettings()));

        await middleware.InvokeAsync(context);
        await responseFeature.FireOnStartingAsync();

        IHeaderDictionary headers = responseFeature.Headers;
        headers["Content-Security-Policy"].ToString()
            .ShouldBe(SecurityHeadersMiddleware.BuildContentSecurityPolicy(AuthAuthority));
        headers["X-Content-Type-Options"].ToString().ShouldBe("nosniff");
        headers["Referrer-Policy"].ToString().ShouldBe("no-referrer");
        headers["X-Frame-Options"].ToString().ShouldBe("DENY");
    }

    [Fact]
    public async Task InvokeAsync_DefersHeaderWritingUntilTheResponseStarts()
    {
        RecordingResponseFeature responseFeature = new();
        DefaultHttpContext context = BuildContext(responseFeature);
        SecurityHeadersMiddleware middleware = new(_ => Task.CompletedTask, Microsoft.Extensions.Options.Options.Create(ValidAuthSettings()));

        await middleware.InvokeAsync(context);

        // Written via OnStarting (not eagerly), so re-executed error/status-code responses are covered too.
        responseFeature.Headers.ContainsKey("Content-Security-Policy").ShouldBeFalse();

        await responseFeature.FireOnStartingAsync();

        responseFeature.Headers.ContainsKey("Content-Security-Policy").ShouldBeTrue();
    }

    private static DefaultHttpContext BuildContext(IHttpResponseFeature responseFeature)
    {
        FeatureCollection features = new();
        features.Set<IHttpRequestFeature>(new HttpRequestFeature());
        features.Set(responseFeature);
        return new DefaultHttpContext(features);
    }

    private static AuthSystemSettings ValidAuthSettings() => new()
    {
        BaseUrl = "https://localhost:5003/",
        Authority = AuthAuthority,
        ClientId = "lotrokoniecdev-web",
        CallbackPath = "/callback",
        SignedOutCallbackPath = "/signout-callback-oidc",
        Scopes = ["openid"]
    };

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
