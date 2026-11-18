using LotroKoniecDev.Application;
using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Features.Patching;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Primitives.Enums;

namespace LotroKoniecDev.Tests.Unit.Tests.Features;

public sealed class PatchIfNeededCommandHandlerTests
{
    private const string DatFilePath = @"C:\LOTRO\client_local_English.dat";
    private const string VersionFilePath = @"C:\data\last_known_game_version.txt";
    private const string TranslationFilePath = @"C:\data\polish.txt";

    private readonly IPatchIfNeededService _service = Substitute.For<IPatchIfNeededService>();
    private readonly IProgress<OperationProgress> _progress = Substitute.For<IProgress<OperationProgress>>();
    private readonly PatchIfNeededCommandHandler _sut;

    [Fact]
    public async Task Handle_NullCommand_ShouldThrow()
    {
        await Should.ThrowAsync<ArgumentNullException>(() => _sut.Handle(null!, CancellationToken.None).AsTask());
    }

    [Theory]
    [InlineData("", VersionFilePath, TranslationFilePath)]
    [InlineData(DatFilePath, "", TranslationFilePath)]
    [InlineData(DatFilePath, VersionFilePath, "")]
    [InlineData(" ", VersionFilePath, TranslationFilePath)]
    public async Task Handle_EmptyPath_ReturnsAValidationFailureAndPatchesNothing(
        string datFilePath, string versionFilePath, string translationFilePath)
    {
        Result<PatchIfNeededResponse> result = await _sut.Handle(
            new PatchIfNeededCommand(datFilePath, versionFilePath, translationFilePath), CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.Type.ShouldBe(ErrorType.Validation);
        result.Error.Code.ShouldBe("PatchIfNeededCommand.Validation");
        _service.DidNotReceive().PatchIfNeeded(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IProgress<OperationProgress>?>());
    }

    [Fact]
    public async Task Handle_ValidCommand_PatchesWithTheInjectedProgressAndReturnsTheServicesAnswer()
    {
        PatchIfNeededResponse response = new(true, true, 90, 10, 0, null);
        _service.PatchIfNeeded(DatFilePath, VersionFilePath, TranslationFilePath, _progress).Returns(Result.Success(response));

        Result<PatchIfNeededResponse> result = await _sut.Handle(
            new PatchIfNeededCommand(DatFilePath, VersionFilePath, TranslationFilePath), CancellationToken.None);

        // The stub answers only for the injected progress, so a patch without it would not come back.
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(response);
    }

    public PatchIfNeededCommandHandlerTests()
    {
        _sut = new PatchIfNeededCommandHandler(_service, _progress, new PatchIfNeededCommandValidator());
    }
}
