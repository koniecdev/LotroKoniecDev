using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Infrastructure.Storage;

namespace LotroKoniecDev.Tests.Infrastructure.Tests;

/// <summary>
/// Exercises the sidecars against real files — the ETag one the conditional request depends on, and
/// the endpoint one that is the sync's outage fallback (#611). Both are read back through the same
/// path the launch uses, because "the value survives a round trip and still parses as a URI" is the
/// property the whole fallback rests on.
/// </summary>
public sealed class TranslationFileCacheTests : IDisposable
{
    private const string Content = "polish content";
    private const string ETag = "\"579BDE6E87308282DEA0FCB1A3E8AF668BF6F558CC4545457C696EFB75F7FD18\"";
    private const string EndpointHref = "https://tms.example.com/api/v1/translation-files/pl";

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"lotro-cache-tests-{Guid.NewGuid():N}");

    private readonly TranslationFileCache _sut = new();

    private string TranslationFilePath => Path.Combine(_directory, "translations", "polish.txt");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void ReadEndpointHref_WithNothingCached_ShouldReturnNull()
    {
        // Act: the first-ever run: the sidecar does not exist yet.
        string? href = _sut.ReadEndpointHref(TranslationFilePath);

        // Assert
        href.ShouldBeNull();
    }

    [Fact]
    public void SaveEndpointHref_ShouldRoundTripAValueThatStillParsesAsAnAbsoluteUri()
    {
        // Arrange: the resolver re-parses whatever comes back, so a round trip that mangles the
        // value (an added BOM, a stripped scheme) would silently disable the outage fallback.
        Result save = _sut.SaveEndpointHref(TranslationFilePath, EndpointHref);

        // Act
        string? href = _sut.ReadEndpointHref(TranslationFilePath);

        // Assert
        save.IsSuccess.ShouldBeTrue();
        href.ShouldBe(EndpointHref);
        Uri.TryCreate(href, UriKind.Absolute, out Uri? parsed).ShouldBeTrue();
        parsed!.ToString().ShouldBe(EndpointHref);
    }

    [Fact]
    public void SaveEndpointHref_ShouldCreateTheDirectoryAndNotDisturbTheTranslationFile()
    {
        // Arrange: the endpoint hint can be written on a 304, when no file is downloaded at all.
        _sut.Save(TranslationFilePath, Content, ETag);

        // Act
        Result save = _sut.SaveEndpointHref(TranslationFilePath, EndpointHref);

        // Assert
        save.IsSuccess.ShouldBeTrue();
        File.ReadAllText(TranslationFilePath).ShouldBe(Content);
        _sut.ReadETag(TranslationFilePath).ShouldBe(ETag);
        _sut.ReadEndpointHref(TranslationFilePath).ShouldBe(EndpointHref);
    }

    [Fact]
    public void SaveEndpointHref_ShouldOverwriteAPreviouslyCachedEndpoint()
    {
        // Arrange: the server legitimately moved the path behind the rel.
        _sut.SaveEndpointHref(TranslationFilePath, EndpointHref);

        // Act
        Result save = _sut.SaveEndpointHref(TranslationFilePath, "https://tms.example.com/downloads/pl.txt");

        // Assert
        save.IsSuccess.ShouldBeTrue();
        _sut.ReadEndpointHref(TranslationFilePath).ShouldBe("https://tms.example.com/downloads/pl.txt");
    }

    [Fact]
    public void ReadEndpointHref_WithABlankSidecar_ShouldReturnSomethingTheResolverTreatsAsNothingCached()
    {
        // Arrange: a truncated or interrupted write leaves an empty file behind, which must read as
        // "no endpoint cached" rather than as an endpoint that happens to be blank.
        Directory.CreateDirectory(Path.GetDirectoryName(TranslationFilePath)!);
        File.WriteAllText(TranslationFilePath + ".endpoint", "   ");

        // Act
        string? href = _sut.ReadEndpointHref(TranslationFilePath);

        // Assert
        string.IsNullOrWhiteSpace(href).ShouldBeTrue();
    }

    [Fact]
    public void ReadEndpointHref_WhenTheSidecarPathIsNotAReadableFile_ShouldDegradeToNothingCached()
    {
        // Arrange: the launch must never die on a broken cache entry; a directory sitting where the
        // sidecar belongs is the portable way to get a path that exists but yields no content.
        // (The unreadable-file case — an ACL that throws UnauthorizedAccessException — is handled by
        // the widened catch in ReadSidecar but cannot be provoked portably, so it is not asserted.)
        Directory.CreateDirectory(TranslationFilePath + ".endpoint");

        // Act
        string? href = _sut.ReadEndpointHref(TranslationFilePath);

        // Assert
        href.ShouldBeNull();
    }
}
