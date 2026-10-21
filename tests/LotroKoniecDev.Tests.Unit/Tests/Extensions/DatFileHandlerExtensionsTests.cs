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
        // Arrange: subfile declaring the VarLen maximum of fragments with no fragment data behind the count
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

    public static TheoryData<string, byte[]> SubFilesThatDoNotWriteBackUnchanged() => new()
    {
        // Parse keeps the last of the two, so the write has one fragment fewer.
        { "a repeated fragment id", TestDataFactory.CreateTextSubFileDataInFileOrder(TextFileId, 5UL, 7UL, 5UL) },
        {
            "a stray byte after the last fragment",
            TestDataFactory.CreateTextSubFileDataWithTrailingBytes(TextFileId, 1, [5UL], [0x42])
        },
        {
            "zero padding after the last fragment",
            TestDataFactory.CreateTextSubFileDataWithTrailingBytes(TextFileId, 1, [5UL], [0x00, 0x00, 0x00, 0x00])
        },
        {
            "a whole fragment after the declared count",
            TestDataFactory.CreateTextSubFileDataWithTrailingBytes(TextFileId, 1, [5UL, 7UL], [])
        },
        // VarLen writes 0-127 in one byte, so the write is one byte shorter.
        { "a fragment count in the two-byte form", TestDataFactory.CreateTextSubFileDataWithTwoByteFragmentCount(TextFileId, 2) },
        // The decoder turns an unpaired surrogate into U+FFFD, so the write puts FD FF back.
        { "a lone high surrogate in a piece", TestDataFactory.CreateTextSubFileDataWithRawPiece(TextFileId, [0x0061, 0xD800, 0x0062]) },
        { "a lone low surrogate in a piece", TestDataFactory.CreateTextSubFileDataWithRawPiece(TextFileId, [0x0061, 0xDC00]) },
        { "a reversed surrogate pair in a piece", TestDataFactory.CreateTextSubFileDataWithRawPiece(TextFileId, [0xDC00, 0xD800]) },
        { "a lone surrogate in an argument string", TestDataFactory.CreateTextSubFileDataWithRawArgString(TextFileId, [0xD800]) },
    };

    [Theory]
    [MemberData(nameof(SubFilesThatDoNotWriteBackUnchanged))]
    public void LoadSubFile_ForAWriteOfASubFileThatWouldNotWriteBackUnchanged_ShouldReturnFailure(
        string scenario,
        byte[] data)
    {
        // Arrange: writing this subfile would lose or change bytes, so a load that may lead to a write
        // must refuse it, and that is the default (#1194).
        IDatFileHandler handler = Substitute.For<IDatFileHandler>();
        handler.GetSubfileData(Handle, TextFileId, data.Length).Returns(Result.Success(data));

        // Act
        Result<SubFile> result = handler.LoadSubFile(Handle, TextFileId, data.Length);

        // Assert
        result.IsFailure.ShouldBeTrue(scenario);
        result.Error.Code.ShouldBe("SubFile.Failed");
        result.Error.Message.ShouldContain("cannot write back exactly");
    }

    [Theory]
    [MemberData(nameof(SubFilesThatDoNotWriteBackUnchanged))]
    public void LoadSubFile_ForAReadOfASubFileThatWouldNotWriteBackUnchanged_ShouldSucceed(
        string scenario,
        byte[] data)
    {
        // Arrange: export only reads. Refusing the subfile there would leave all of its rows out of
        // exported.txt, and the next import would mark them removed (#1194).
        IDatFileHandler handler = Substitute.For<IDatFileHandler>();
        handler.GetSubfileData(Handle, TextFileId, data.Length).Returns(Result.Success(data));

        // Act
        Result<SubFile> result = handler.LoadSubFile(Handle, TextFileId, data.Length, readOnly: true);

        // Assert
        result.IsSuccess.ShouldBeTrue(scenario);
    }

    [Fact]
    public void LoadSubFile_ForAWriteOfASubFileWithIdsOutOfKeyOrder_ShouldSucceed()
    {
        // Arrange: almost half of the live DAT's text subfiles store their ids unsorted, so the check
        // must pass when the fragments are written back in file order.
        IDatFileHandler handler = Substitute.For<IDatFileHandler>();
        byte[] data = TestDataFactory.CreateTextSubFileDataInFileOrder(TextFileId, 9UL, 3UL, 5UL);
        handler.GetSubfileData(Handle, TextFileId, data.Length).Returns(Result.Success(data));

        // Act
        Result<SubFile> result = handler.LoadSubFile(Handle, TextFileId, data.Length);

        // Assert
        result.IsSuccess.ShouldBeTrue();
    }
}
