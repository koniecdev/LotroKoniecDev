using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Features.TranslationFileSyncing;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;
using Microsoft.Extensions.Logging.Abstractions;

namespace LotroKoniecDev.Tests.Unit.Tests.Features;

/// <summary>
/// The CLI ships to players' machines and cannot be updated remotely, so it resolves its download
/// URL from the TMS service document by rel instead of carrying a route (ADR-0041 / #611). These
/// tests pin what the resolver does when the document is missing, silent, or hostile.
/// </summary>
public sealed class TranslationFileEndpointResolverTests
{
    private const string BaseUrl = "https://tms.example.com";
    private const string DownloadHref = "https://tms.example.com/api/v1/translation-files/pl";
    private const string CachedHref = "https://tms.example.com/api/v1/translation-files/pl-cached";

    private readonly ITranslationSystemDiscoveryClient _discoveryClient =
        Substitute.For<ITranslationSystemDiscoveryClient>();

    private readonly TranslationFileEndpointResolver _sut;

    public TranslationFileEndpointResolverTests()
    {
        _sut = new TranslationFileEndpointResolver(
            _discoveryClient, NullLogger<TranslationFileEndpointResolver>.Instance);
    }

    private void DiscoveryReturns(params DiscoveredLink[] links) =>
        _discoveryClient.FetchLinksAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<DiscoveredLink>>(links));

    private void DiscoveryFails(string message = "connection refused") =>
        _discoveryClient.FetchLinksAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<IReadOnlyList<DiscoveredLink>>(
                DomainErrors.TranslationFileSync.NetworkError(message)));

    private static DiscoveredLink TranslationFileLink(string href = DownloadHref) =>
        new(href, TranslationFileEndpointResolver.TranslationFileRel, "GET");

    [Fact]
    public async Task ResolveAsync_WhenDiscoveryAdvertisesTheRel_ShouldReturnItsHref()
    {
        // Arrange: the document carries other entry points too; only the rel decides.
        DiscoveryReturns(
            new DiscoveredLink($"{BaseUrl}/", "self", "GET"),
            TranslationFileLink(),
            new DiscoveredLink($"{BaseUrl}/api/v1/progress", "progress", "GET"));

        // Act
        Result<Uri> result = await _sut.ResolveAsync(BaseUrl, cachedHref: null, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ToString().ShouldBe(DownloadHref);
    }

    [Fact]
    public async Task ResolveAsync_WhenDiscoveryIsUnreachableAndAnHrefIsCached_ShouldFallBackToIt()
    {
        // Arrange: the cache is the outage safety net: the sync keeps working while the root is down.
        DiscoveryFails();

        // Act
        Result<Uri> result = await _sut.ResolveAsync(BaseUrl, CachedHref, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ToString().ShouldBe(CachedHref);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    // A sidecar left behind by a truncated write reads back as blank, which is "nothing cached",
    // not "an endpoint that happens to be empty".
    [InlineData("   ")]
    public async Task ResolveAsync_WhenDiscoveryIsUnreachableAndNothingIsCached_ShouldFailWithoutGuessingAPath(
        string? cachedHref)
    {
        // Arrange: the first-ever run against an unreachable server: there is nothing safe to guess.
        DiscoveryFails();

        // Act
        Result<Uri> result = await _sut.ResolveAsync(BaseUrl, cachedHref, CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(DomainErrors.TranslationFileSync.EndpointDiscoveryUnavailableCode);
        result.Error.Message.ShouldContain("never guessed");
    }

    [Fact]
    public async Task ResolveAsync_WhenTheDocumentDoesNotAdvertiseTheRel_ShouldFailWithoutUsingTheCache()
    {
        // Arrange: the server answered and did not offer the action. That is a statement about
        // what is on offer, not an outage, so the cached href must not paper over it.
        DiscoveryReturns(new DiscoveredLink($"{BaseUrl}/", "self", "GET"));

        // Act
        Result<Uri> result = await _sut.ResolveAsync(BaseUrl, CachedHref, CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(DomainErrors.TranslationFileSync.EndpointNotAdvertisedCode);
    }

    [Theory]
    [InlineData("https://evil.example.com/api/v1/translation-files/pl")]
    [InlineData("https://tms.example.com.evil.com/api/v1/translation-files/pl")]
    [InlineData("https://tms.example.com:8443/api/v1/translation-files/pl")]
    [InlineData("http://tms.example.com/api/v1/translation-files/pl")]
    [InlineData("file:///etc/passwd")]
    [InlineData("/api/v1/translation-files/pl")]
    [InlineData("not a uri at all")]
    // The well-known ways to get past an origin check. Each one is rejected because of a particular
    // System.Uri rule, so they are pinned here: rewriting the check to use uri.Host, to
    // AbsoluteUri.StartsWith(baseUrl) or to IdnHost quietly reopens one of them.
    // User info: the authority is everything after the '@', so this would fetch from evil.com.
    [InlineData("https://tms.example.com@evil.com/api/v1/translation-files/pl")]
    [InlineData("https://tms.example.com:443@evil.com/api/v1/translation-files/pl")]
    // A trailing dot: the same DNS name, but a different authority string.
    [InlineData("https://tms.example.com./api/v1/translation-files/pl")]
    // A Cyrillic 'а' (U+0430) in "example": it looks the same but is a different host.
    [InlineData("https://tms.exаmple.com/api/v1/translation-files/pl")]
    public async Task ResolveAsync_WhenTheAdvertisedHrefLeavesTheConfiguredOrigin_ShouldRejectIt(string href)
    {
        // Arrange: the href is attacker-influenceable whenever the base URL is, so the document may
        // move the path but never the origin, and never off TLS.
        DiscoveryReturns(TranslationFileLink(href));

        // Act
        Result<Uri> result = await _sut.ResolveAsync(BaseUrl, cachedHref: null, CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(DomainErrors.TranslationFileSync.EndpointRejectedCode);
    }

    [Theory]
    // A different case in the host, or the default port written out, is the same origin. Rejecting those
    // would break a perfectly normal deployment, so what we accept is pinned as carefully as what we
    // reject.
    [InlineData(BaseUrl, "https://TMS.EXAMPLE.COM/api/v1/translation-files/pl")]
    [InlineData(BaseUrl, "https://tms.example.com:443/api/v1/translation-files/pl")]
    // A non-default port is fine as long as both sides agree on it.
    [InlineData("https://tms.example.com:8443", "https://tms.example.com:8443/api/v1/translation-files/pl")]
    // The document may change the path freely. That is the whole point of resolving by rel.
    [InlineData(BaseUrl, "https://tms.example.com/downloads/pl.txt")]
    public async Task ResolveAsync_WhenTheAdvertisedHrefStaysOnTheConfiguredOrigin_ShouldAcceptIt(
        string baseUrl, string href)
    {
        // Arrange
        DiscoveryReturns(new DiscoveredLink(href, TranslationFileEndpointResolver.TranslationFileRel, "GET"));

        // Act
        Result<Uri> result = await _sut.ResolveAsync(baseUrl, cachedHref: null, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ToString().ShouldBe(new Uri(href).ToString());
    }

    [Fact]
    public async Task ResolveAsync_WhenTheCachedHrefLeavesTheConfiguredOrigin_ShouldRejectItToo()
    {
        // Arrange: the sidecar is on-disk data, and it is stale the moment --tms-url is repointed.
        DiscoveryFails();

        // Act
        Result<Uri> result = await _sut.ResolveAsync(
            BaseUrl, "https://evil.example.com/api/v1/translation-files/pl", CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(DomainErrors.TranslationFileSync.EndpointDiscoveryUnavailableCode);
        result.Error.Message.ShouldContain("different origin");
    }

    [Theory]
    [InlineData("http://localhost:5002")]
    [InlineData("http://127.0.0.1:5002")]
    public async Task ResolveAsync_WhenTheDeploymentIsLoopback_ShouldAllowPlainHttp(string baseUrl)
    {
        // Arrange: loopback has no network hop, the one place the TLS rule bends (dev host Kestrel).
        DiscoveryReturns(new DiscoveredLink(
            $"{baseUrl}/api/v1/translation-files/pl", TranslationFileEndpointResolver.TranslationFileRel, "GET"));

        // Act
        Result<Uri> result = await _sut.ResolveAsync(baseUrl, cachedHref: null, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ToString().ShouldBe($"{baseUrl}/api/v1/translation-files/pl");
    }

    [Fact]
    public async Task ResolveAsync_WhenTheRelIsAdvertisedForAnotherVerb_ShouldNotTreatItAsTheDownload()
    {
        // Arrange: a rel names an action together with its method, so a POST link is a different one.
        DiscoveryReturns(new DiscoveredLink(
            DownloadHref, TranslationFileEndpointResolver.TranslationFileRel, "POST"));

        // Act
        Result<Uri> result = await _sut.ResolveAsync(BaseUrl, cachedHref: null, CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(DomainErrors.TranslationFileSync.EndpointNotAdvertisedCode);
    }

    [Fact]
    public async Task ResolveAsync_WhenTheBaseUrlIsNotAnAbsoluteUri_ShouldFailWithoutCallingDiscovery()
    {
        // Act
        Result<Uri> result = await _sut.ResolveAsync("tms.example.com", cachedHref: null, CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(DomainErrors.TranslationFileSync.EndpointRejectedCode);
        await _discoveryClient.DidNotReceive().FetchLinksAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
