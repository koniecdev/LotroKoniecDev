using LotroKoniecDev.Logging.Redaction;

namespace LotroKoniecDev.Logging.Tests.Unit.Redaction;

public sealed class SensitiveDataRedactorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void RedactQueryString_EmptyOrNull_ReturnsEmptyString(string? queryString)
    {
        string result = SensitiveDataRedactor.RedactQueryString(queryString);

        result.ShouldBe(string.Empty);
    }

    [Theory]
    [InlineData("?code=abc123", "?code=***")]
    [InlineData("?token=abc123", "?token=***")]
    [InlineData("?access_token=abc123", "?access_token=***")]
    [InlineData("?refresh_token=abc123", "?refresh_token=***")]
    [InlineData("?id_token=abc123", "?id_token=***")]
    [InlineData("?password=hunter2", "?password=***")]
    [InlineData("?pwd=hunter2", "?pwd=***")]
    [InlineData("?secret=abc123", "?secret=***")]
    [InlineData("?client_secret=abc123", "?client_secret=***")]
    public void RedactQueryString_SensitiveKey_RedactsValue(string queryString, string expected)
    {
        string result = SensitiveDataRedactor.RedactQueryString(queryString);

        result.ShouldBe(expected);
    }

    [Theory]
    [InlineData("?CODE=abc123", "?CODE=***")]
    [InlineData("?Token=abc123", "?Token=***")]
    [InlineData("?Client_Secret=abc123", "?Client_Secret=***")]
    public void RedactQueryString_SensitiveKeyDifferentCasing_StillRedactsValue(string queryString, string expected)
    {
        string result = SensitiveDataRedactor.RedactQueryString(queryString);

        result.ShouldBe(expected);
    }

    [Theory]
    [InlineData("?%63ode=secret", "?%63ode=***")]
    [InlineData("?access%5Ftoken=abc123", "?access%5Ftoken=***")]
    [InlineData("?CLIENT%5Fsecret=abc", "?CLIENT%5Fsecret=***")]
    public void RedactQueryString_PercentEncodedSensitiveKey_StillRedactsValue(string queryString, string expected)
    {
        string result = SensitiveDataRedactor.RedactQueryString(queryString);

        result.ShouldBe(expected);
    }

    [Theory]
    [InlineData("?id_token_hint=eyJ.abc.def", "?id_token_hint=***")]
    [InlineData("?client_assertion=eyJ.abc.def", "?client_assertion=***")]
    public void RedactQueryString_OidcCredentialBearingKey_RedactsValue(string queryString, string expected)
    {
        string result = SensitiveDataRedactor.RedactQueryString(queryString);

        result.ShouldBe(expected);
    }

    [Fact]
    public void RedactQueryString_BareQuestionMark_ReturnsEmptyString()
    {
        string result = SensitiveDataRedactor.RedactQueryString("?");

        result.ShouldBe(string.Empty);
    }

    [Fact]
    public void RedactQueryString_RepeatedSensitiveKey_RedactsEveryOccurrence()
    {
        string result = SensitiveDataRedactor.RedactQueryString("?code=first&code=second");

        result.ShouldBe("?code=***&code=***");
    }

    [Fact]
    public void RedactQueryString_OnlyRedactsSensitiveKeysAndLeavesOthersIntact()
    {
        string result = SensitiveDataRedactor.RedactQueryString("?lang=pl&code=abc123&page=2");

        result.ShouldBe("?lang=pl&code=***&page=2");
    }

    [Fact]
    public void RedactQueryString_NonSensitiveKey_IsLeftUnchanged()
    {
        string result = SensitiveDataRedactor.RedactQueryString("?lang=pl&page=2");

        result.ShouldBe("?lang=pl&page=2");
    }

    [Fact]
    public void RedactQueryString_ValueContainingEqualsSign_RedactsWholeValue()
    {
        string result = SensitiveDataRedactor.RedactQueryString("?code=aGVsbG8=d29ybGQ=");

        result.ShouldBe("?code=***");
    }

    [Fact]
    public void RedactQueryString_EmailValue_MasksLocalPart()
    {
        string result = SensitiveDataRedactor.RedactQueryString("?email=alice@example.com");

        result.ShouldBe("?email=a***@example.com");
    }

    /// <summary>
    /// The shape a link built with <c>Uri.EscapeDataString</c> puts on the wire (ADR-0046). The query
    /// is matched raw and never decoded, so a matcher that only knows the literal <c>@</c> lets the
    /// whole address through into the request log.
    /// </summary>
    [Theory]
    [InlineData("?email=alice%40example.com", "?email=a***%40example.com")]
    [InlineData("?email=alice%2Btag%40example.com", "?email=a***%40example.com")]
    [InlineData("?email=alice%40example.com&page=2", "?email=a***%40example.com&page=2")]
    public void RedactQueryString_PercentEncodedEmailValue_MasksLocalPart(string queryString, string expected)
    {
        string result = SensitiveDataRedactor.RedactQueryString(queryString);

        result.ShouldBe(expected);
    }

    [Fact]
    public void RedactQueryString_EmailInArbitraryParameterName_IsStillMasked()
    {
        string result = SensitiveDataRedactor.RedactQueryString("?username=bob.smith@contoso.co.uk&page=1");

        result.ShouldBe("?username=b***@contoso.co.uk&page=1");
    }

    [Fact]
    public void RedactQueryString_SensitiveKeyTakesPrecedenceOverEmailMasking()
    {
        string result = SensitiveDataRedactor.RedactQueryString("?login_hint=alice@example.com&code=alice@example.com");

        result.ShouldBe("?login_hint=a***@example.com&code=***");
    }

    [Fact]
    public void RedactQueryString_KeyWithoutValue_IsPreserved()
    {
        string result = SensitiveDataRedactor.RedactQueryString("?refresh&page=2");

        result.ShouldBe("?refresh&page=2");
    }

    [Theory]
    [InlineData("alice@example.com", "a***@example.com")]
    [InlineData("b@c.com", "b***@c.com")]
    [InlineData("bob.smith@contoso.co.uk", "b***@contoso.co.uk")]
    [InlineData("alice+tag@example.com", "a***@example.com")]
    [InlineData("alice%40example.com", "a***%40example.com")]
    [InlineData("alice%2Btag%40example.com", "a***%40example.com")]
    public void MaskEmail_ValidEmail_MasksEverythingAfterTheFirstCharacterOfTheLocalPart(
        string email,
        string expected)
    {
        string result = SensitiveDataRedactor.MaskEmail(email);

        result.ShouldBe(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("@example.com")]
    [InlineData("%40example.com")]
    [InlineData("not-an-email")]
    public void MaskEmail_MissingLocalPartOrAtSign_ReturnsFullyRedacted(string value)
    {
        string result = SensitiveDataRedactor.MaskEmail(value);

        result.ShouldBe("***");
    }
}
