using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using LotroKoniecDev.TranslationSystem.API.Auth.Provisioning;
using LotroKoniecDev.TranslationSystem.API.Tests.Unit.Shared;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.Repositories;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.Abstractions;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Auth;

public sealed class TranslatorProvisionerTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 14, 0, 0, 0, TimeSpan.Zero);
    private static readonly IdentityId Identity = IdentityId.Create();

    // ITranslatorRepository / IUnitOfWork are genuine public boundaries (stubbed); the current-user
    // accessor is an internal interface NSubstitute can't proxy, so it gets a hand-written double.
    private readonly ITranslatorRepository _translatorRepository = Substitute.For<ITranslatorRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();

    private static Translator Existing(string displayName = "Strider")
        => Translator.Create(Identity, DisplayName.Create(displayName).Value, email: null, Now).Value;

    private TranslatorProvisioner CreateProvisioner(StubCurrentUserAccessor accessor)
        => new(accessor, _translatorRepository, _unitOfWork, new FixedTimeProvider(Now));

    private static StubCurrentUserAccessor Accessor(
        ValueMaybe<IdentityId>? identity = null,
        string? username = "Aragorn",
        string? email = "aragorn@gondor.test")
        => new(identity ?? ValueMaybe<IdentityId>.From(Identity), username, email);

    [Fact]
    public async Task ProvisionCurrentAsync_WhenNoIdentity_ShouldReturnForbiddenAndNotPersist()
    {
        // Arrange — a token without a parseable subject must never be attributed.
        TranslatorProvisioner provisioner = CreateProvisioner(Accessor(ValueMaybe<IdentityId>.None()));

        // Act
        Result<TranslatorId> result = await provisioner.ProvisionCurrentAsync(CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Translators.Unauthenticated");
        _translatorRepository.DidNotReceive().Insert(Arg.Any<Translator>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProvisionCurrentAsync_WhenNeitherNameNorEmailClaim_ShouldFailValidationAndNotPersist()
    {
        // Arrange — an authenticated token carrying no display-name material; the VO must reject it.
        TranslatorProvisioner provisioner = CreateProvisioner(Accessor(username: null, email: null));
        _translatorRepository.GetByIdentityIdAsync(Identity, Arg.Any<CancellationToken>())
            .Returns(Maybe<Translator>.None);

        // Act
        Result<TranslatorId> result = await provisioner.ProvisionCurrentAsync(CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("TranslatorEntity.DisplayName.NullOrEmpty");
        _translatorRepository.DidNotReceive().Insert(Arg.Any<Translator>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProvisionCurrentAsync_WhenNoEmailClaim_ShouldFallBackToEmaillessProfile()
    {
        // Arrange — only the email claim is missing; the name claim still yields a valid display name.
        TranslatorProvisioner provisioner = CreateProvisioner(Accessor(email: null));
        _translatorRepository.GetByIdentityIdAsync(Identity, Arg.Any<CancellationToken>())
            .Returns(Maybe<Translator>.None);
        Translator? inserted = null;
        _translatorRepository.When(repository => repository.Insert(Arg.Any<Translator>()))
            .Do(callInfo => inserted = callInfo.Arg<Translator>());

        // Act
        Result<TranslatorId> result = await provisioner.ProvisionCurrentAsync(CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        inserted.ShouldNotBeNull();
        inserted.DisplayName.Value.ShouldBe("Aragorn");
        inserted.Email.ShouldBeNull();
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProvisionCurrentAsync_WhenEmailClaimMalformed_ShouldDiscardItAndProvisionEmaillessProfile()
    {
        // Arrange — a present-but-invalid email claim is non-essential to attribution: it is discarded
        // (the write still proceeds), rather than failing the whole provisioning.
        TranslatorProvisioner provisioner = CreateProvisioner(Accessor(email: "not-an-email"));
        _translatorRepository.GetByIdentityIdAsync(Identity, Arg.Any<CancellationToken>())
            .Returns(Maybe<Translator>.None);
        Translator? inserted = null;
        _translatorRepository.When(repository => repository.Insert(Arg.Any<Translator>()))
            .Do(callInfo => inserted = callInfo.Arg<Translator>());

        // Act
        Result<TranslatorId> result = await provisioner.ProvisionCurrentAsync(CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        inserted.ShouldNotBeNull();
        inserted.DisplayName.Value.ShouldBe("Aragorn");
        inserted.Email.ShouldBeNull();
    }

    [Fact]
    public async Task ProvisionCurrentAsync_WhenAlreadyProvisioned_ShouldRefreshAndReturnExistingIdWithoutInsert()
    {
        // Arrange — a renamed account: the existing row converges on the latest claims, no new row.
        Translator existing = Existing("Strider");
        TranslatorId existingId = existing.Id;
        _translatorRepository.GetByIdentityIdAsync(Identity, Arg.Any<CancellationToken>())
            .Returns(Maybe<Translator>.From(existing));
        TranslatorProvisioner provisioner = CreateProvisioner(Accessor());

        // Act
        Result<TranslatorId> result = await provisioner.ProvisionCurrentAsync(CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(existingId);
        existing.DisplayName.Value.ShouldBe("Aragorn");
        _translatorRepository.DidNotReceive().Insert(Arg.Any<Translator>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProvisionCurrentAsync_WhenAlreadyProvisionedAndClaimsUnchanged_ShouldReturnExistingIdWithoutWriting()
    {
        // Arrange — the row already carries exactly the current claims. Provisioning now runs on every
        // authenticated request (ADR-0004 amendment), so an unchanged re-touch must be a pure read: no
        // refresh write on the hot path.
        Translator existing = Translator.Create(
            Identity,
            DisplayName.Create("Aragorn").Value,
            Email.Create("aragorn@gondor.test").Value,
            Now).Value;
        TranslatorId existingId = existing.Id;
        _translatorRepository.GetByIdentityIdAsync(Identity, Arg.Any<CancellationToken>())
            .Returns(Maybe<Translator>.From(existing));
        TranslatorProvisioner provisioner = CreateProvisioner(Accessor());

        // Act
        Result<TranslatorId> result = await provisioner.ProvisionCurrentAsync(CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(existingId);
        _translatorRepository.DidNotReceive().Insert(Arg.Any<Translator>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProvisionCurrentAsync_WhenNew_ShouldInsertAndReturnNewId()
    {
        // Arrange
        _translatorRepository.GetByIdentityIdAsync(Identity, Arg.Any<CancellationToken>())
            .Returns(Maybe<Translator>.None);
        Translator? inserted = null;
        _translatorRepository.When(repository => repository.Insert(Arg.Any<Translator>()))
            .Do(callInfo => inserted = callInfo.Arg<Translator>());
        TranslatorProvisioner provisioner = CreateProvisioner(Accessor());

        // Act
        Result<TranslatorId> result = await provisioner.ProvisionCurrentAsync(CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        inserted.ShouldNotBeNull();
        inserted.IdentityId.ShouldBe(Identity);
        result.Value.ShouldBe(inserted.Id);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProvisionCurrentAsync_WhenConcurrentFirstWriteRaces_ShouldReReadCommittedRowAndSucceed()
    {
        // Arrange — another request inserted the row between our read and save: the unique index
        // rejects the duplicate (DbUpdateException), and we must re-read the committed row, not fail.
        Translator raced = Existing("Strider");
        TranslatorId racedId = raced.Id;
        Translator? rejectedInsert = null;
        _translatorRepository.When(repository => repository.Insert(Arg.Any<Translator>()))
            .Do(call => rejectedInsert = call.Arg<Translator>());
        _translatorRepository.GetByIdentityIdAsync(Identity, Arg.Any<CancellationToken>())
            .Returns(Maybe<Translator>.None, Maybe<Translator>.From(raced));
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(_ => throw new DbUpdateException("unique violation"), _ => Task.FromResult(1));
        TranslatorProvisioner provisioner = CreateProvisioner(Accessor());

        // Act
        Result<TranslatorId> result = await provisioner.ProvisionCurrentAsync(CancellationToken.None);

        // Assert — the committed row's id is returned and it converged on the current claims; the
        // rejected insert is dropped from tracking so it cannot re-fire on the shared unit of work.
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(racedId);
        raced.DisplayName.Value.ShouldBe("Aragorn");
        rejectedInsert.ShouldNotBeNull();
        _translatorRepository.Received(1).Detach(rejectedInsert);
        await _unitOfWork.Received(2).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProvisionCurrentAsync_WhenSaveFailsAndRowStillMissing_ShouldRethrow()
    {
        // Arrange — a DbUpdateException that is NOT a duplicate-row race (e.g. a real DB error): with
        // no committed row to re-read, the exception must propagate, never be swallowed.
        _translatorRepository.GetByIdentityIdAsync(Identity, Arg.Any<CancellationToken>())
            .Returns(Maybe<Translator>.None, Maybe<Translator>.None);
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new DbUpdateException("transient failure"));
        TranslatorProvisioner provisioner = CreateProvisioner(Accessor());

        // Act + Assert
        await Should.ThrowAsync<DbUpdateException>(
            async () => await provisioner.ProvisionCurrentAsync(CancellationToken.None));
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTimeProvider(DateTimeOffset now)
        {
            _now = now;
        }

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
