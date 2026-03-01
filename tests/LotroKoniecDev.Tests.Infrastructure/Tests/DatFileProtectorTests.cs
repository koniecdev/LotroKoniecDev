using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Infrastructure.DatFile;

namespace LotroKoniecDev.Tests.Infrastructure.Tests;

public sealed class DatFileProtectorTests : IDisposable
{
    private readonly DatFileProtector _sut = new();
    private readonly string _tempFile;

    public DatFileProtectorTests()
    {
        _tempFile = Path.GetTempFileName();
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile))
        {
            File.SetAttributes(_tempFile, FileAttributes.Normal);
            File.Delete(_tempFile);
        }
    }

    [Fact]
    public void Protect_ShouldSetReadOnlyAttribute()
    {
        // Act
        Result result = _sut.Protect(_tempFile);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        File.GetAttributes(_tempFile).HasFlag(FileAttributes.ReadOnly).ShouldBeTrue();
    }

    [Fact]
    public void Unprotect_ShouldRemoveReadOnlyAttribute()
    {
        // Arrange
        File.SetAttributes(_tempFile, File.GetAttributes(_tempFile) | FileAttributes.ReadOnly);

        // Act
        Result result = _sut.Unprotect(_tempFile);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        File.GetAttributes(_tempFile).HasFlag(FileAttributes.ReadOnly).ShouldBeFalse();
    }

    [Fact]
    public void IsProtected_ShouldReturnTrue_WhenFileIsReadOnly()
    {
        // Arrange
        File.SetAttributes(_tempFile, File.GetAttributes(_tempFile) | FileAttributes.ReadOnly);

        // Act
        Result<bool> result = _sut.IsProtected(_tempFile);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeTrue();
    }

    [Fact]
    public void IsProtected_ShouldReturnFalse_WhenFileIsNotReadOnly()
    {
        // Act
        Result<bool> result = _sut.IsProtected(_tempFile);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeFalse();
    }

    [Fact]
    public void IsProtected_ShouldReturnFailure_WhenFileDoesNotExist()
    {
        // Arrange
        string nonExistentPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

        // Act
        Result<bool> result = _sut.IsProtected(nonExistentPath);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("DatFileProtection.IsProtectedFailed");
    }

    [Fact]
    public void Protect_ShouldBeIdempotent_WhenCalledTwice()
    {
        // Act
        _sut.Protect(_tempFile);
        Result result = _sut.Protect(_tempFile);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        File.GetAttributes(_tempFile).HasFlag(FileAttributes.ReadOnly).ShouldBeTrue();
    }

    [Fact]
    public void Unprotect_ShouldBeIdempotent_WhenFileNotReadOnly()
    {
        // Act
        Result result = _sut.Unprotect(_tempFile);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        File.GetAttributes(_tempFile).HasFlag(FileAttributes.ReadOnly).ShouldBeFalse();
    }

    [Fact]
    public void Protect_ShouldReturnFailure_WhenFileDoesNotExist()
    {
        // Arrange
        string nonExistentPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

        // Act
        Result result = _sut.Protect(nonExistentPath);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("DatFileProtection.ProtectFailed");
    }

    [Fact]
    public void Unprotect_ShouldReturnFailure_WhenFileDoesNotExist()
    {
        // Arrange
        string nonExistentPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

        // Act
        Result result = _sut.Unprotect(nonExistentPath);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("DatFileProtection.UnprotectFailed");
    }

    [Fact]
    public void Protect_ShouldThrow_WhenPathIsNull()
    {
        Should.Throw<ArgumentException>(() => _sut.Protect(null!));
    }

    [Fact]
    public void Unprotect_ShouldThrow_WhenPathIsNull()
    {
        Should.Throw<ArgumentException>(() => _sut.Unprotect(null!));
    }

    [Fact]
    public void IsProtected_ShouldThrow_WhenPathIsNull()
    {
        Should.Throw<ArgumentException>(() => _sut.IsProtected(null!));
    }

    [Fact]
    public void Protect_ShouldPreserveOtherAttributes()
    {
        // Arrange
        File.SetAttributes(_tempFile, FileAttributes.Archive);

        // Act
        _sut.Protect(_tempFile);

        // Assert
        FileAttributes attributes = File.GetAttributes(_tempFile);
        attributes.HasFlag(FileAttributes.ReadOnly).ShouldBeTrue();
        attributes.HasFlag(FileAttributes.Archive).ShouldBeTrue();
    }
}
