using LotroKoniecDev.Application.Abstractions.DatFilesServices;
using LotroKoniecDev.Application.Extensions;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Domain.Models;
using LotroKoniecDev.Primitives.Enums;
using LotroKoniecDev.Tests.Unit.Shared;

namespace LotroKoniecDev.Tests.Unit.Tests.Extensions;

public sealed class DatFileHandlerExtensionsTests
{
    private const int Handle = 1;
    private const int TextFileId = 0x25000001;

    [Fact]
    public void LoadSubFile_SubFileDeclaringImpossibleFragmentCount_ShouldReturnParseFailure()
    {
        // Arrange — subfile declaring the VarLen maximum of fragments with no fragment data behind the count
        IDatFileHandler handler = Substitute.For<IDatFileHandler>();
        byte[] corruptData = TestDataFactory.CreateTextSubFileDataWithImpossibleFragmentCount(TextFileId, 0x7FFF);
        handler.GetSubfileData(Handle, TextFileId, corruptData.Length).Returns(Result.Success(corruptData));

        // Act
        Result<SubFile> result = handler.LoadSubFile(Handle, TextFileId, corruptData.Length);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("SubFile.Failed");
        result.Error.Type.ShouldBe(ErrorType.Failure);
        result.Error.Message.ShouldContain("fragment");
    }

    [Fact]
    public void LoadSubFile_ValidSubFile_ShouldReturnParsedSubFile()
    {
        // Arrange
        IDatFileHandler handler = Substitute.For<IDatFileHandler>();
        byte[] validData = TestDataFactory.CreateTextSubFileData(TextFileId, "Test");
        handler.GetSubfileData(Handle, TextFileId, validData.Length).Returns(Result.Success(validData));

        // Act
        Result<SubFile> result = handler.LoadSubFile(Handle, TextFileId, validData.Length);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.FragmentCount.ShouldBe(1);
        result.Value.Fragments[1UL].GetFullText().ShouldBe("Test");
    }
}
