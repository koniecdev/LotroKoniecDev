using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;

namespace LotroKoniecDev.TranslationSystem.Domain.Tests.Unit.Tests.Aggregates.TranslatorAggregate;

public sealed class TranslatorTests
{
    private static readonly DateTimeOffset Provisioned = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly IdentityId Identity = IdentityId.Create();

    private static DisplayName Name(string value = "Aragorn") => DisplayName.Create(value).Value;
    private static Email Mail(string value = "aragorn@gondor.test") => Email.Create(value).Value;

    [Fact]
    public void Create_WithValidInputs_ShouldProvisionTranslatorWithProvisionedAt()
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
        translator.Id.Value.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public void Create_WithoutEmail_ShouldProvisionWithNullEmail()
    {
        // Act: the email claim may be absent; the lean profile does not require it.
        Result<Translator> result = Translator.Create(Identity, Name(), email: null, Provisioned);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Email.ShouldBeNull();
    }

    [Fact]
    public void Create_WithEmptyIdentity_ShouldThrow()
    {
        // Assert: IdentityId is the cross-context key; an empty one is a programmer error.
        Should.Throw<ArgumentException>(() => Translator.Create(default, Name(), Mail(), Provisioned));
    }

    [Fact]
    public void RefreshProfile_ShouldUpdateNameAndEmailWithoutTouchingProvisionedAt()
    {
        // Arrange
        Translator translator = Translator.Create(Identity, Name("Strider"), null, Provisioned).Value;

        // Act: a renamed account converges on the next authenticated touch.
        translator.RefreshProfile(Name("Aragorn"), Mail());

        // Assert
        translator.DisplayName.Value.ShouldBe("Aragorn");
        translator.Email.ShouldNotBeNull();
        translator.Email.Value.ShouldBe("aragorn@gondor.test");
        translator.ProvisionedAt.ShouldBe(Provisioned);
        translator.IdentityId.ShouldBe(Identity);
    }

    [Fact]
    public void RefreshProfile_WithNullEmail_ShouldClearKnownEmail()
    {
        // Arrange
        Translator translator = Translator.Create(Identity, Name(), Mail(), Provisioned).Value;

        // Act: the email claim disappeared (or became malformed) on a later touch.
        translator.RefreshProfile(Name(), email: null);

        // Assert
        translator.Email.ShouldBeNull();
    }

    [Theory]
    [InlineData("aragorn@gondor.test")]
    [InlineData(null)]
    public void Erase_ShouldReplaceTheNameAndClearTheEmail(string? email)
    {
        // Arrange
        Translator translator = Translator.Create(
            Identity, Name(), email is null ? null : Mail(email), Provisioned).Value;

        // Act
        translator.Erase();

        // Assert
        translator.DisplayName.Value.ShouldBe("Usunięte konto");
        translator.Email.ShouldBeNull();
    }

    [Fact]
    public void Erase_ShouldKeepTheIdentityAndTheProvisioningTime()
    {
        // Arrange
        Translator translator = Translator.Create(Identity, Name(), Mail(), Provisioned).Value;
        TranslatorId id = translator.Id;

        // Act
        translator.Erase();

        // Assert: the translations stay credited to this profile, so its keys must not move.
        translator.Id.ShouldBe(id);
        translator.IdentityId.ShouldBe(Identity);
        translator.ProvisionedAt.ShouldBe(Provisioned);
    }

    [Fact]
    public void Erase_Twice_ShouldGiveTheSameProfileAsOnce()
    {
        // Arrange
        Translator translator = Translator.Create(Identity, Name(), Mail(), Provisioned).Value;
        translator.Erase();

        // Act: a redelivered message erases the profile again.
        translator.Erase();

        // Assert
        translator.DisplayName.Value.ShouldBe("Usunięte konto");
        translator.Email.ShouldBeNull();
    }
}
