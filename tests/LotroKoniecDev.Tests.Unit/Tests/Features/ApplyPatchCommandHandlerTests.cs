using LotroKoniecDev.Application;
using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Features.Patching;
using LotroKoniecDev.Domain.Core.BuildingBlocks;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Primitives.Enums;

namespace LotroKoniecDev.Tests.Unit.Tests.Features;

public sealed class ApplyPatchCommandHandlerTests
{
    private readonly IPatchingService _patchingService;
    private readonly ApplyPatchCommandHandler _sut;

    public ApplyPatchCommandHandlerTests()
    {
        _patchingService = Substitute.For<IPatchingService>();
        IProgress<OperationProgress> progress = Substitute.For<IProgress<OperationProgress>>();
        _sut = new ApplyPatchCommandHandler(_patchingService, progress);
    }

    [Fact]
    public async Task Handle_NullCommand_ShouldThrow()
    {
        await Should.ThrowAsync<ArgumentNullException>(
            () => _sut.Handle(null!, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Handle_Success_ShouldDelegateToPatchingService()
    {
        // Arrange
        PatchSummaryResponse summary = new(100, 95, 5, []);
        _patchingService.ApplyTranslations("/translations/polish.txt", "/game/client_local_English.dat", Arg.Any<IProgress<OperationProgress>?>())
            .Returns(Result.Success(summary));

        ApplyPatchCommand command = new("/translations/polish.txt", "/game/client_local_English.dat");

        // Act
        Result<PatchSummaryResponse> result = await _sut.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(summary);
    }

    [Fact]
    public async Task Handle_PatchingFails_ShouldReturnFailure()
    {
        // Arrange
        Error error = new("Translation.ParseError", "Bad format", ErrorType.Validation);
        _patchingService.ApplyTranslations(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IProgress<OperationProgress>?>())
            .Returns(Result.Failure<PatchSummaryResponse>(error));

        ApplyPatchCommand command = new("/translations/polish.txt", "/game/client_local_English.dat");

        // Act
        Result<PatchSummaryResponse> result = await _sut.Handle(command, CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Translation.ParseError");
    }
}
