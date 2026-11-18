using LotroKoniecDev.Application.Features.AppUpdating;

namespace LotroKoniecDev.Tests.Unit.Tests.Features;

public sealed class DesktopVersionTests
{
    [Theory]
    [InlineData("0.0.0", 0, 0, 0)]
    [InlineData("0.1.0", 0, 1, 0)]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("10.20.30", 10, 20, 30)]
    [InlineData("999999999.0.999999999", 999999999, 0, 999999999)]
    public void TryParse_ThreePlainNumbers_ReadsEachPart(string value, int major, int minor, int patch)
    {
        // Act
        bool parsed = DesktopVersion.TryParse(value, out DesktopVersion? version);

        // Assert
        parsed.ShouldBeTrue();
        version.ShouldBe(new DesktopVersion(major, minor, patch));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("1")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("01.2.3")]
    [InlineData("1.02.3")]
    [InlineData("1.2.03")]
    [InlineData("1.2.3-rc1")]
    [InlineData("1.2.3+abc")]
    [InlineData("v1.2.3")]
    [InlineData(" 1.2.3")]
    [InlineData("1.2.3 ")]
    [InlineData("1.2.3\n")]
    [InlineData("1..3")]
    [InlineData("-1.2.3")]
    [InlineData("1.2.x")]
    [InlineData("1000000000.0.0")]
    [InlineData("١.٢.٣")]
    [InlineData("１.２.３")]
    public void TryParse_AnythingElse_IsRefused(string? value)
    {
        // Act
        bool parsed = DesktopVersion.TryParse(value, out DesktopVersion? version);

        // Assert
        parsed.ShouldBeFalse();
        version.ShouldBeNull();
    }

    [Theory]
    [InlineData("0.1.1", "0.1.0", true)]
    [InlineData("0.2.0", "0.1.9", true)]
    [InlineData("1.0.0", "0.99.99", true)]
    [InlineData("0.10.0", "0.9.0", true)]
    [InlineData("0.1.0", "0.1.0", false)]
    [InlineData("0.1.0", "0.1.1", false)]
    [InlineData("0.9.0", "0.10.0", false)]
    public void IsNewerThan_TwoVersions_ComparesPartByPart(string candidate, string installed, bool expected)
    {
        // Arrange
        DesktopVersion.TryParse(candidate, out DesktopVersion? candidateVersion).ShouldBeTrue();
        DesktopVersion.TryParse(installed, out DesktopVersion? installedVersion).ShouldBeTrue();

        // Act
        bool isNewer = candidateVersion.IsNewerThan(installedVersion);

        // Assert
        isNewer.ShouldBe(expected);
    }

    [Theory]
    [InlineData(0, 0, 0, true)]
    [InlineData(0, 0, 1, false)]
    [InlineData(0, 1, 0, false)]
    public void IsDevelopmentBuild_AnyVersion_IsTrueOnlyForZeroZeroZero(int major, int minor, int patch, bool expected)
    {
        new DesktopVersion(major, minor, patch).IsDevelopmentBuild.ShouldBe(expected);
    }

    [Fact]
    public void ToString_AnyVersion_WritesThePlainForm()
    {
        new DesktopVersion(1, 20, 300).ToString().ShouldBe("1.20.300");
    }
}
