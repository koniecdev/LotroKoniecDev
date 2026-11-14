using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Primitives.Enums;

namespace LotroKoniecDev.Tests.Unit.Tests.Features;

/// <summary>
/// Every way the game file itself can refuse a write gets its own error and its own advice (#1248).
/// </summary>
public sealed class DatFileWriteCheckTests
{
    private const string OsReason = "The process cannot access the file because it is being used by another process.";

    private static readonly string DatFilePath =
        Path.Combine(Path.GetTempPath(), "lotro", "client_local_English.dat");

    [Fact]
    public void ToResult_NoRefusal_ShouldSucceed()
    {
        // Arrange
        DatFileWriteCheck check = new(DatFileWriteRefusal.None);

        // Act
        Result result = check.ToResult(DatFilePath);

        // Assert
        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void ToResult_DefaultValue_ShouldSucceed()
    {
        // Arrange: what a test substitute that nobody set up returns.
        DatFileWriteCheck check = default;

        // Act
        Result result = check.ToResult(DatFilePath);

        // Assert
        result.IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData(DatFileWriteRefusal.ReadOnly, "DatFile.ReadOnly", ErrorType.IoError)]
    [InlineData(DatFileWriteRefusal.AccessDenied, "DatFile.WriteAccessDenied", ErrorType.IoError)]
    [InlineData(DatFileWriteRefusal.InUse, "DatFile.InUse", ErrorType.IoError)]
    [InlineData(DatFileWriteRefusal.Other, "DatFile.CannotOpenForWriting", ErrorType.IoError)]
    [InlineData(DatFileWriteRefusal.NotFound, "DatFile.NotFound", ErrorType.NotFound)]
    public void ToResult_Refusal_ShouldFailWithItsOwnCodeAndType(
        DatFileWriteRefusal refusal,
        string expectedCode,
        ErrorType expectedType)
    {
        // Arrange
        DatFileWriteCheck check = new(refusal, OsReason);

        // Act
        Result result = check.ToResult(DatFilePath);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(expectedCode);
        result.Error.Type.ShouldBe(expectedType);
    }

    [Theory]
    [InlineData(DatFileWriteRefusal.ReadOnly)]
    [InlineData(DatFileWriteRefusal.AccessDenied)]
    [InlineData(DatFileWriteRefusal.InUse)]
    [InlineData(DatFileWriteRefusal.Other)]
    [InlineData(DatFileWriteRefusal.NotFound)]
    public void ToResult_Refusal_ShouldNameTheFullPath(DatFileWriteRefusal refusal)
    {
        // Arrange: a relative path, as a player may pass with -d.
        string relativePath = Path.Combine("lotro", "client_local_English.dat");
        DatFileWriteCheck check = new(refusal, OsReason);

        // Act
        Result result = check.ToResult(relativePath);

        // Assert
        result.Error.Message.ShouldContain(Path.GetFullPath(relativePath));
    }

    [Fact]
    public void ToResult_ReadOnly_ShouldSayHowToClearTheFlag()
    {
        // Arrange
        DatFileWriteCheck check = new(DatFileWriteRefusal.ReadOnly);

        // Act
        Result result = check.ToResult(DatFilePath);

        // Assert
        result.Error.Message.ShouldContain("is marked read-only");
        result.Error.Message.ShouldContain("clear the Read-only check box");
    }

    [Fact]
    public void ToResult_AccessDenied_ShouldAdviseElevationAndACauseForAnElevatedUser()
    {
        // Arrange: a launch without elevation lands here on a stock install, and so does an elevated
        // run that a changed ACL or a security program refuses.
        DatFileWriteCheck check = new(DatFileWriteRefusal.AccessDenied);

        // Act
        Result result = check.ToResult(DatFilePath);

        // Assert
        result.Error.Message.ShouldContain("Run the patcher as administrator");
        result.Error.Message.ShouldContain("Security tab");
        result.Error.Message.ShouldContain("antivirus");
    }

    [Fact]
    public void ToResult_InUse_ShouldNotRuleOutTheGame()
    {
        // Arrange: the game check swallows its own errors, so the game itself can still hold the file.
        DatFileWriteCheck check = new(DatFileWriteRefusal.InUse);

        // Act
        Result result = check.ToResult(DatFilePath);

        // Assert
        result.Error.Message.ShouldContain("Another program has the game file");
        result.Error.Message.ShouldContain("including the game");
    }

    [Fact]
    public void ToResult_Other_ShouldCarryTheSystemReason()
    {
        // Arrange
        DatFileWriteCheck check = new(DatFileWriteRefusal.Other, OsReason);

        // Act
        Result result = check.ToResult(DatFilePath);

        // Assert
        result.Error.Message.ShouldEndWith($"for writing: {OsReason}");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ToResult_OtherWithNoSystemReason_ShouldEndTheSentenceCleanly(string? osReason)
    {
        // Arrange
        DatFileWriteCheck check = new(DatFileWriteRefusal.Other, osReason);

        // Act
        Result result = check.ToResult(DatFilePath);

        // Assert
        result.Error.Message.ShouldBe($"The patcher cannot open the game file '{DatFilePath}' for writing.");
    }

    [Fact]
    public void ToResult_UnnamedRefusalValue_ShouldFailWithTheSystemReason()
    {
        // Arrange: a value the switch does not name still refuses, it never passes.
        DatFileWriteCheck check = new((DatFileWriteRefusal)99, OsReason);

        // Act
        Result result = check.ToResult(DatFilePath);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("DatFile.CannotOpenForWriting");
        result.Error.Message.ShouldEndWith($"for writing: {OsReason}");
    }
}
