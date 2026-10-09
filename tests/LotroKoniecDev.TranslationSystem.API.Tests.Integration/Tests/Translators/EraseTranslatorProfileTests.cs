using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using LotroKoniecDev.SharedKernel.Authorization;
using LotroKoniecDev.SharedKernel.Enums;
using LotroKoniecDev.SharedKernel.Messaging;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using LotroKoniecDev.TranslationSystem.API.Features.Translators;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.WriteDbContexts;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.Translators;

/// <summary>
/// The TMS half of an account erasure (ADR-0065): after it, the database holds neither the person's name
/// nor their address, and the editor credits their translations to „Usunięte konto”.
/// </summary>
[Collection("TranslationApi")]
public sealed class EraseTranslatorProfileTests : IAsyncLifetime
{
    private const int FileId = 620756992;
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly TranslationSystemApiFactory _factory;
    private Guid _erasedIdentity;
    private TranslatorId _erasedId;
    private TranslatorId _otherId;
    private TranslationId _translationId;

    public EraseTranslatorProfileTests(TranslationSystemApiFactory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync(
            "TRUNCATE translation.\"Translations\", translation.\"GameVersions\", translation.\"Translators\" CASCADE;");

        _erasedIdentity = Guid.NewGuid();

        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();

        GameVersion gameVersion = GameVersion.Create(LotroNotationVersion.Create("48.0").Value, Now).Value;
        dbContext.GameVersions.Add(gameVersion);

        Translator erased = Translator.Create(
            IdentityId.FromValue(_erasedIdentity),
            DisplayName.Create("Frodo Baggins").Value,
            Email.Create("frodo@shire.me").Value,
            Now).Value;
        Translator other = Translator.Create(
            IdentityId.Create(),
            DisplayName.Create("Samwise Gamgee").Value,
            Email.Create("sam@shire.me").Value,
            Now).Value;
        dbContext.Translators.AddRange(erased, other);

        Translation translation = Translation.CreateUntranslated(
            FragmentKey.Create(FileId, 1001).Value,
            TranslationSource.Create("Source text", null, null).Value,
            gameVersion.Id,
            Now).Value;
        translation.ProvideTranslation("Polski tekst", erased.Id, Now);
        translation.Approve(other.Id, Now);
        dbContext.Translations.Add(translation);

        await dbContext.SaveChangesAsync();
        _erasedId = erased.Id;
        _otherId = other.Id;
        _translationId = translation.Id;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Handle_ForAnAccountWithAProfile_ShouldRemoveTheNameAndTheEmail()
    {
        // Act
        Result result = await EraseAsync(_erasedIdentity);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        Translator profile = await ReadProfileAsync(_erasedId);
        profile.DisplayName.Value.ShouldBe("Usunięte konto");
        profile.Email.ShouldBeNull();
        profile.IdentityId.Value.ShouldBe(_erasedIdentity);
    }

    [Fact]
    public async Task Handle_ForAnAccountWithAProfile_ShouldCreditItsTranslationsToTheErasedName()
    {
        // Act
        await EraseAsync(_erasedIdentity);
        HttpResponseMessage response = await TranslatorClient().GetAsync($"/api/v1/translations/{_translationId.Value}");

        // Assert: the translation stays, and the editor no longer says whose it was.
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        TranslationDetailResponse? body = await response.Content.ReadFromJsonAsync<TranslationDetailResponse>(JsonOptions);
        body.ShouldNotBeNull();
        body.Submitter.ShouldNotBeNull();
        body.Submitter.Id.ShouldBe(_erasedId);
        body.Submitter.DisplayName.ShouldBe("Usunięte konto");
        body.Approver.ShouldNotBeNull();
        body.Approver.DisplayName.ShouldBe("Samwise Gamgee");
    }

    [Fact]
    public async Task Handle_ShouldLeaveEveryOtherProfileAsItWas()
    {
        // Act
        await EraseAsync(_erasedIdentity);

        // Assert
        Translator other = await ReadProfileAsync(_otherId);
        other.DisplayName.Value.ShouldBe("Samwise Gamgee");
        other.Email.ShouldNotBeNull();
        other.Email.Value.ShouldBe("sam@shire.me");
    }

    [Fact]
    public async Task Handle_Twice_ShouldSucceedAndKeepTheProfileErased()
    {
        // Arrange: the broker may deliver the same event twice.
        await EraseAsync(_erasedIdentity);

        // Act
        Result result = await EraseAsync(_erasedIdentity);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        Translator profile = await ReadProfileAsync(_erasedId);
        profile.DisplayName.Value.ShouldBe("Usunięte konto");
        profile.Email.ShouldBeNull();
    }

    [Fact]
    public async Task Handle_ForAnAccountThatNeverOpenedTheTms_ShouldSucceedAndCreateNoProfile()
    {
        // Act
        Result result = await EraseAsync(Guid.NewGuid());

        // Assert
        result.IsSuccess.ShouldBeTrue();
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
        (await dbContext.Translators.CountAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task Handle_WithAnEmptyIdentity_ShouldFailValidationAndChangeNothing()
    {
        // Act
        Result result = await EraseAsync(Guid.Empty);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Type.ShouldBe(TypeOfError.Validation);
        Translator profile = await ReadProfileAsync(_erasedId);
        profile.DisplayName.Value.ShouldBe("Frodo Baggins");
    }

    private async Task<Result> EraseAsync(Guid identity)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ICommandHandler<EraseTranslatorProfile.Command, Result> handler = scope.ServiceProvider
            .GetRequiredService<ICommandHandler<EraseTranslatorProfile.Command, Result>>();

        return await handler.Handle(
            new EraseTranslatorProfile.Command(IdentityId.FromValue(identity)),
            CancellationToken.None);
    }

    private async Task<Translator> ReadProfileAsync(TranslatorId id)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
        return await dbContext.Translators.AsNoTracking().SingleAsync(translator => translator.Id == id);
    }

    private HttpClient TranslatorClient()
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TranslationSystemApiFactory.CreateAccessToken(AuthConstants.Roles.Translator));
        return client;
    }
}
