using LotroKoniecDev.Primitives.Constants;

namespace LotroKoniecDev.Tests.Unit.Tests.Core;

public sealed class TmsConstantsTests
{
    [Fact]
    public void DefaultBaseUrl_WhenParsed_ShouldBeABareHttpsServerRoot()
    {
        // Act
        bool isAbsolute = Uri.TryCreate(TmsConstants.DefaultBaseUrl, UriKind.Absolute, out Uri? uri);

        // Assert: the patcher finds every endpoint by rel from the root (ADR-0041), so a path here
        // would be a hardcoded endpoint.
        isAbsolute.ShouldBeTrue();
        uri!.Scheme.ShouldBe(Uri.UriSchemeHttps);
        uri.AbsolutePath.ShouldBe("/");
        uri.Query.ShouldBeEmpty();
        uri.Fragment.ShouldBeEmpty();
    }
}
