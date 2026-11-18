using LotroKoniecDev.Application.Features.AppUpdating;

namespace LotroKoniecDev.Tests.Unit.Tests.Features;

public sealed class DesktopReleasePolicyTests
{
    private const string InstallerName = "LotroPoPolsku-Setup-0.2.0.exe";

    private const string Hash = "4f0c3c6a3b0e6f3e9c1d2b7a8e5f4c3b2a1908f7e6d5c4b3a29180f7e6d5c4b3";

    private static readonly DesktopVersion Version = new(0, 2, 0);

    [Fact]
    public void ReleasesListUri_ReadsTheFirstPageOfTheList_NeverLatest()
    {
        // Owner decision on #45: "Latest" is shared with the server's releases.
        DesktopReleasePolicy.ReleasesListUri.ToString()
            .ShouldBe("https://api.github.com/repos/koniecdev/LotroKoniecDev/releases?per_page=100");
    }

    [Fact]
    public void AssetNames_ForAVersion_FollowTheReleaseContract()
    {
        DesktopReleasePolicy.InstallerAssetName(Version).ShouldBe(InstallerName);
        DesktopReleasePolicy.ChecksumAssetName(Version).ShouldBe(InstallerName + ".sha256");
    }

    [Fact]
    public void DownloadUri_ForAnAsset_IsGitHubsOwnReleaseAddress()
    {
        DesktopReleasePolicy.DownloadUri(Version, InstallerName).ToString().ShouldBe(
            "https://github.com/koniecdev/LotroKoniecDev/releases/download/desktop-v0.2.0/LotroPoPolsku-Setup-0.2.0.exe");
    }

    [Theory]
    [InlineData("desktop-v0.1.0", 0, 1, 0)]
    [InlineData("desktop-v12.3.45", 12, 3, 45)]
    public void TryParseTag_ADesktopTag_ReadsItsVersion(string tag, int major, int minor, int patch)
    {
        // Act
        bool parsed = DesktopReleasePolicy.TryParseTag(tag, out DesktopVersion? version);

        // Assert
        parsed.ShouldBeTrue();
        version.ShouldBe(new DesktopVersion(major, minor, patch));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v0.1.0")]
    [InlineData("0.1.0")]
    [InlineData("desktop-v")]
    [InlineData("desktop-v01.0.0")]
    [InlineData("desktop-v0.1.0-rc1")]
    [InlineData("Desktop-v0.1.0")]
    [InlineData("desktop-V0.1.0")]
    [InlineData("desktop-v 0.1.0")]
    [InlineData("xdesktop-v0.1.0")]
    public void TryParseTag_AnyOtherTag_IsRefused(string? tag)
    {
        // Act
        bool parsed = DesktopReleasePolicy.TryParseTag(tag, out DesktopVersion? version);

        // Assert
        parsed.ShouldBeFalse();
        version.ShouldBeNull();
    }

    [Theory]
    [InlineData("https://github.com/koniecdev/LotroKoniecDev/releases/download/x")]
    [InlineData("https://api.github.com/repos/koniecdev/LotroKoniecDev/releases")]
    [InlineData("https://release-assets.githubusercontent.com/github-production-release-asset/1")]
    [InlineData("https://objects.githubusercontent.com/x")]
    [InlineData("https://GITHUB.com/x")]
    [InlineData("https://github.com:443/x")]
    public void IsAllowedHost_GitHubOverHttps_IsAccepted(string address)
    {
        DesktopReleasePolicy.IsAllowedHost(new Uri(address)).ShouldBeTrue();
    }

    [Theory]
    [InlineData("http://github.com/x")]
    [InlineData("http://release-assets.githubusercontent.com/x")]
    [InlineData("https://github.com:8443/x")]
    [InlineData("https://user@github.com/x")]
    [InlineData("https://user:password@github.com/x")]
    [InlineData("https://evil.com/x")]
    [InlineData("https://github.com.evil.com/x")]
    [InlineData("https://githubusercontent.com.evil.com/x")]
    [InlineData("https://evilgithubusercontent.com/x")]
    [InlineData("https://githubusercontent.com/x")]
    [InlineData("https://gist.github.com/x")]
    [InlineData("https://github.com./x")]
    [InlineData("ftp://github.com/x")]
    [InlineData("file:///C:/github.com/x")]
    public void IsAllowedHost_AnythingElse_IsRefused(string address)
    {
        DesktopReleasePolicy.IsAllowedHost(new Uri(address)).ShouldBeFalse();
    }

    [Fact]
    public void IsAllowedHost_NoAddressOrARelativeOne_IsRefused()
    {
        DesktopReleasePolicy.IsAllowedHost(null).ShouldBeFalse();
        DesktopReleasePolicy.IsAllowedHost(new Uri("/releases", UriKind.Relative)).ShouldBeFalse();
    }

    [Theory]
    [InlineData(Hash + "  " + InstallerName + "\n")]
    [InlineData(Hash + "  " + InstallerName)]
    [InlineData(Hash + "  " + InstallerName + "\r\n")]
    [InlineData(Hash + " *" + InstallerName + "\n")]
    [InlineData(Hash + "  " + InstallerName + "  \n\n")]
    public void TryParseChecksumFile_ASha256sumLineForTheInstaller_ReadsTheHash(string content)
    {
        // Act
        bool parsed = DesktopReleasePolicy.TryParseChecksumFile(content, InstallerName, out string? hash);

        // Assert
        parsed.ShouldBeTrue();
        hash.ShouldBe(Hash);
    }

    [Fact]
    public void TryParseChecksumFile_UpperCaseHex_IsAccepted()
    {
        // Act
        bool parsed = DesktopReleasePolicy.TryParseChecksumFile(
            Hash.ToUpperInvariant() + "  " + InstallerName + "\n", InstallerName, out string? hash);

        // Assert
        parsed.ShouldBeTrue();
        hash.ShouldBe(Hash.ToUpperInvariant());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(Hash)]
    [InlineData(Hash + "  ")]
    [InlineData(Hash + "  LotroPoPolsku-Setup-0.1.0.exe\n")]
    [InlineData(Hash + "  other.exe\n")]
    [InlineData(Hash + "  " + InstallerName + ".sha256\n")]
    [InlineData(Hash + "\t" + InstallerName + "\n")]
    [InlineData(Hash + " " + InstallerName + "\n")]
    [InlineData("4f0c" + "  " + InstallerName + "\n")]
    [InlineData(Hash + "0  " + InstallerName + "\n")]
    [InlineData("zz0c3c6a3b0e6f3e9c1d2b7a8e5f4c3b2a1908f7e6d5c4b3a29180f7e6d5c4b3  " + InstallerName + "\n")]
    [InlineData(Hash + "  " + InstallerName + "\n" + Hash + "  " + InstallerName + "\n")]
    [InlineData(" " + Hash + "  " + InstallerName + "\n")]
    public void TryParseChecksumFile_AnythingElse_IsRefused(string? content)
    {
        // Act
        bool parsed = DesktopReleasePolicy.TryParseChecksumFile(content, InstallerName, out string? hash);

        // Assert
        parsed.ShouldBeFalse();
        hash.ShouldBeNull();
    }

    [Theory]
    [InlineData(@"C:\Users\Anna\AppData\Roaming\LotroPoPolsku\update-install.log")]
    [InlineData(@"C:\Users\Zażółć Gęślą\AppData\Roaming\LotroPoPolsku\update-install.log")]
    public void InstallerArguments_AnyLogPath_IsThePinnedStringWithThePathInQuotes(string logPath)
    {
        // Act
        string arguments = DesktopReleasePolicy.InstallerArguments(logPath);

        // Assert: Inno Setup reads a quote as on/off with no escape, so the path, spaces included,
        // reaches it whole only as one quoted token.
        arguments.ShouldBe(
            $"/SILENT /NOCANCEL /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /FORCECLOSEAPPLICATIONS \"/LOG={logPath}\"");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("C:\\a\"b\\update-install.log")]
    public void InstallerArguments_ABlankPathOrOneWithAQuote_Throws(string logPath)
    {
        Should.Throw<ArgumentException>(() => DesktopReleasePolicy.InstallerArguments(logPath));
    }

    [Fact]
    public void TimeLimits_AreTheOnesTheSpecGives()
    {
        // Spec 0016: the whole check has 10 seconds, the installer download 10 minutes.
        DesktopReleasePolicy.CheckTimeLimit.ShouldBe(TimeSpan.FromSeconds(10));
        DesktopReleasePolicy.DownloadTimeLimit.ShouldBe(TimeSpan.FromMinutes(10));
    }

    [Fact]
    public void InstallerStartVerb_IsRunAs()
    {
        // The only check of the verb that CI runs: the start itself is tested on Windows only (#1450).
        DesktopReleasePolicy.InstallerStartVerb.ShouldBe("runas");
    }
}
