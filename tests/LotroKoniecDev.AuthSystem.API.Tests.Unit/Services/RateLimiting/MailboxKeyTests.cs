using LotroKoniecDev.AuthSystem.API.Services.RateLimiting;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Services.RateLimiting;

/// <summary>
/// The key every mail budget counts on (#835, ADR-0057). Spellings one inbox receives must share a key;
/// addresses that can be two different inboxes may share one only where the fold is documented.
/// </summary>
public sealed class MailboxKeyTests
{
    [Theory]
    [InlineData("ANNA+1@GMAIL.COM")]
    [InlineData("ANNA+LOTRO+2@GMAIL.COM")]
    [InlineData("A.NNA@GMAIL.COM")]
    [InlineData("A.N.N.A@GMAIL.COM")]
    [InlineData("ANNA@GOOGLEMAIL.COM")]
    [InlineData("A.NN.A+TAG@GOOGLEMAIL.COM")]
    [InlineData("anna@gmail.com")]
    [InlineData("a.nna+Tag@GoogleMail.com")]
    public void FromNormalizedEmail_ShouldFoldGmailSpellingsIntoOneMailbox(string spelling)
    {
        // Act
        MailboxKey key = MailboxKey.FromNormalizedEmail(spelling);

        // Assert
        key.ShouldBe(MailboxKey.FromNormalizedEmail("ANNA@GMAIL.COM"));
        key.Value.ShouldBe("ANNA@GMAIL.COM");
    }

    [Fact]
    public void FromNormalizedEmail_ShouldFoldACombiningAccentLikeIdentityDoes()
    {
        // Arrange: "józef" written with a combining acute, and with the composed letter (#692)
        const string decomposed = "jo\u0301zef@wp.pl";
        const string composed = "J\u00D3ZEF@WP.PL";

        // Act
        MailboxKey decomposedKey = MailboxKey.FromNormalizedEmail(decomposed);

        // Assert
        decomposedKey.ShouldBe(MailboxKey.FromNormalizedEmail(composed));
    }

    [Fact]
    public void ToString_ShouldMaskTheAddress()
    {
        // Act
        string text = MailboxKey.FromNormalizedEmail("ANNA+1@GMAIL.COM").ToString();

        // Assert
        text.ShouldBe("A***@GMAIL.COM");
    }

    [Theory]
    [InlineData("ANNA+1@EXAMPLE.COM")]
    [InlineData("ANNA+@EXAMPLE.COM")]
    [InlineData("ANNA+A.B-C@EXAMPLE.COM")]
    [InlineData("anna+x@example.com")]
    public void FromNormalizedEmail_ShouldDropThePlusTagForEveryDomain(string spelling)
    {
        // Act
        MailboxKey key = MailboxKey.FromNormalizedEmail(spelling);

        // Assert
        key.Value.ShouldBe("ANNA@EXAMPLE.COM");
    }

    [Theory]
    [InlineData("ANNA@EXAMPLE.COM", "A.NNA@EXAMPLE.COM")]
    [InlineData("ANNA@EXAMPLE.COM", "ANNA@EXAMPLE.ORG")]
    [InlineData("ANNA@GMAIL.COM", "ANNA@MAIL.GMAIL.COM")]
    [InlineData("ANNA@GMAIL.COM", "ANNA@GMAIL.CO")]
    [InlineData("ANNA_1@EXAMPLE.COM", "ANNA-1@EXAMPLE.COM")]
    [InlineData("ANNA@EXAMPLE.COM", "BOB+ANNA@EXAMPLE.COM")]
    [InlineData("+ANNA@EXAMPLE.COM", "+BOB@EXAMPLE.COM")]
    public void FromNormalizedEmail_ShouldKeepAddressesApart_WhenNoDocumentedRuleFoldsThem(
        string first,
        string second)
    {
        // Act
        MailboxKey firstKey = MailboxKey.FromNormalizedEmail(first);
        MailboxKey secondKey = MailboxKey.FromNormalizedEmail(second);

        // Assert: dots count outside Gmail, and a subdomain or a look-alike domain is another provider
        firstKey.ShouldNotBe(secondKey);
    }

    [Theory]
    [InlineData("+ANNA@EXAMPLE.COM")]
    [InlineData("+ANNA+X@EXAMPLE.COM")]
    [InlineData("+ANNA+X+Y@EXAMPLE.COM")]
    public void FromNormalizedEmail_ShouldKeepALeadingPlus_AndStillDropTheTagAfterIt(string spelling)
    {
        // Act: a '+' in first place has no mailbox name in front of it, so it is part of the name
        MailboxKey key = MailboxKey.FromNormalizedEmail(spelling);

        // Assert
        key.Value.ShouldBe("+ANNA@EXAMPLE.COM");
    }

    [Theory]
    [InlineData("ANNA+X")]
    [InlineData("@GMAIL.COM")]
    [InlineData("A.NNA+X@")]
    public void FromNormalizedEmail_ShouldKeepAnAddressWithoutAMailboxNameOrADomainAsItIs(string malformed)
    {
        // Act: the validators refuse these first, so the key only has to stay stable, not fold them
        MailboxKey key = MailboxKey.FromNormalizedEmail(malformed);

        // Assert
        key.Value.ShouldBe(malformed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FromNormalizedEmail_ShouldRejectAnAddressThatWouldShareOneBudgetWithEveryCaller(string? normalizedEmail)
    {
        // Act / Assert: the handlers validate the address first, so an empty one is a programmer error
        Should.Throw<ArgumentException>(() => MailboxKey.FromNormalizedEmail(normalizedEmail));
    }
}
