using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.API.Features.TranslationFiles;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Features.TranslationFiles;

public sealed class GetTranslationFileHandlerTests
{
    private const string AskedHash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string NewerHash = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";

    [Theory]
    [InlineData("de")]
    [InlineData("")]
    [InlineData("pl-PL")]
    public async Task Handle_WhenLanguageUnsupported_ShouldReturnValidationError(string language)
    {
        // Arrange
        GetTranslationFile.Handler handler = new(new StubDiskCache(copy: null));

        // Act
        Result<GetTranslationFile.TranslationFileResult> result =
            await handler.Handle(new GetTranslationFile.Query(language, AskedHash), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("TranslationFiles.UnsupportedLanguage");
    }

    [Fact]
    public async Task Handle_WhenNoFileWasBuilt_ShouldReturnNotFound()
    {
        // Arrange: the artifact row is gone between the hash lookup and the copy.
        GetTranslationFile.Handler handler = new(new StubDiskCache(copy: null));

        // Act
        Result<GetTranslationFile.TranslationFileResult> result =
            await handler.Handle(new GetTranslationFile.Query("pl", AskedHash), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("TranslationFiles.NotFound");
    }

    [Fact]
    public async Task Handle_WhenTheCopyIsNewerThanAsked_ShouldTagTheBodyWithTheCopysHash()
    {
        // Arrange: a rebuild landed after the hash lookup, so the cache hands out a newer copy. The
        // ETag must name the bytes that are sent, or the patcher's integrity check refuses them.
        await using MemoryStream body = new("2||1||Nowszy||NULL||NULL||1"u8.ToArray());
        GetTranslationFile.Handler handler = new(new StubDiskCache(new TranslationFileCopy(body, NewerHash)));

        // Act
        Result<GetTranslationFile.TranslationFileResult> result =
            await handler.Handle(new GetTranslationFile.Query("pl", AskedHash), CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ETag.ShouldBe(NewerHash);
        result.Value.Content.ShouldBeSameAs(body);
    }

    [Theory]
    [InlineData("pl")]
    [InlineData("PL")]
    [InlineData("Pl")]
    public async Task Handle_WithAnyCasingOfTheLanguage_ShouldAskTheCacheForTheSupportedCode(string language)
    {
        // Arrange: the route accepts "PL", but the copy's file name is built from the supported code
        // and never from the route value. The stub answers only for "pl".
        await using MemoryStream body = new("1||1||Alfa||NULL||NULL||1"u8.ToArray());
        GetTranslationFile.Handler handler = new(new StubDiskCache(new TranslationFileCopy(body, AskedHash)));

        // Act
        Result<GetTranslationFile.TranslationFileResult> result =
            await handler.Handle(new GetTranslationFile.Query(language, AskedHash), CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ETag.ShouldBe(AskedHash);
    }

    /// <summary>
    /// NSubstitute cannot proxy an internal interface of this assembly, so the seam gets a hand-written
    /// stub, as in <see cref="TranslationFileRebuildWorkerTests"/>.
    /// </summary>
    private sealed class StubDiskCache : ITranslationFileDiskCache
    {
        private readonly TranslationFileCopy? _copy;

        public StubDiskCache(TranslationFileCopy? copy)
        {
            _copy = copy;
        }

        public Task<TranslationFileCopy?> OpenAsync(string language, string contentHash, CancellationToken cancellationToken)
            => Task.FromResult(language == "pl" && contentHash == AskedHash ? _copy : null);
    }
}
