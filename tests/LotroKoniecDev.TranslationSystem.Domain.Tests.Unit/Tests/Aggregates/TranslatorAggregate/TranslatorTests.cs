using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.ValueObjects;

namespace LotroKoniecDev.TranslationSystem.Domain.Tests.Unit.Tests.Aggregates.TranslatorAggregate;

public sealed class TranslatorTests
{
    private static readonly DateTimeOffset Provisioned = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SeenAgain = new(2026, 6, 14, 9, 30, 0, TimeSpan.Zero);
    private static readonly IdentityId Identity = IdentityId.Create();

    private static DisplayName Name(string value = "Aragorn") => DisplayName.Create(value).Value;
    private static Email Mail(string value = "aragorn@gondor.test") => Email.Create(value).Value;

    [Fact]
    public void Create_WithValidInputs_ShouldProvisionWithMatchingTimestamps()
    {
        // Act
        Result<Translator> result = Translator.Create(Identity, Name(), Mail(), Provisioned);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        Translator translator = result.Value;
        translator.IdentityId.ShouldBe(Identity);
        translator.DisplayName.Value.ShouldBe("Aragorn");
        translator.Email.ShouldNotBeNull();
        translator.Email.Value.ShouldBe("aragorn@gondor.test");
        translator.ProvisionedAt.ShouldBe(Provisioned);
        translator.LastSeenAt.ShouldBe(Provisioned);
        translator.Id.Value.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public void Create_WithoutEmail_ShouldProvisionWithNullEmail()
    {
        // Act — the email claim may be absent; the lean profile does not require it.
        Result<Translator> result = Translator.Create(Identity, Name(), email: null, Provisioned);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Email.ShouldBeNull();
    }

    [Fact]
    public void Create_WithEmptyIdentity_ShouldThrow()
    {
        // Assert — IdentityId is the cross-context key; an empty one is a programmer error.
        Should.Throw<ArgumentException>(() => Translator.Create(default, Name(), Mail(), Provisioned));
    }

    [Fact]
    public void RefreshProfile_ShouldUpdateNameEmailAndStampLastSeen()
    {
        // Arrange
        Translator translator = Translator.Create(Identity, Name("Strider"), null, Provisioned).Value;

        // Act — a renamed account converges on the next authenticated touch.
        translator.RefreshProfile(Name("Aragorn"), Mail(), SeenAgain);

        // Assert
        translator.DisplayName.Value.ShouldBe("Aragorn");
        translator.Email.ShouldNotBeNull();
        translator.Email.Value.ShouldBe("aragorn@gondor.test");
        translator.LastSeenAt.ShouldBe(SeenAgain);
        translator.ProvisionedAt.ShouldBe(Provisioned);
        translator.IdentityId.ShouldBe(Identity);
    }

    [Fact]
    public void RefreshProfile_WithNullEmail_ShouldClearKnownEmail()
    {
        // Arrange
        Translator translator = Translator.Create(Identity, Name(), Mail(), Provisioned).Value;

        // Act — the email claim disappeared (or became malformed) on a later touch.
        translator.RefreshProfile(Name(), email: null, SeenAgain);

        // Assert
        translator.Email.ShouldBeNull();
    }
}
