using System.Collections.Generic;
using System.Linq.Expressions;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.ReadDbContexts;
using LotroKoniecDev.TranslationSystem.ReadModels.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.ReadModels.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.ReadModels.Aggregates.TranslatorAggregate;
using LotroKoniecDev.TranslationSystem.ReadModels.Projections;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Shared;

/// <summary>
/// A pure in-memory <see cref="IApplicationReadDbContext"/> double for unit-testing read-side
/// handlers: <see cref="Translations"/> is always populated, <see cref="GameVersions"/> and
/// <see cref="Translators"/> optionally (the public progress snapshot reads the former, the GDPR
/// contribution export reads the latter). EF Core's async operators run via
/// <see cref="TestAsyncQueryProvider{T}"/>.
/// </summary>
internal sealed class FakeReadDbContext : IApplicationReadDbContext
{
    private readonly List<TranslationReadModel> _translations;
    private readonly List<GameVersionReadModel> _gameVersions;
    private readonly List<TranslatorReadModel> _translators;

    public FakeReadDbContext(
        IEnumerable<TranslationReadModel> translations,
        IEnumerable<GameVersionReadModel>? gameVersions = null,
        IEnumerable<TranslatorReadModel>? translators = null)
    {
        _translations = [.. translations];
        _gameVersions = [.. gameVersions ?? []];
        _translators = [.. translators ?? []];
    }

    public DbSet<GameVersionReadModel> GameVersions => BuildSet(_gameVersions);

    public DbSet<TranslationReadModel> Translations => BuildSet(_translations);

    public DbSet<PrecomputedTranslationFileReadModel> PrecomputedTranslationFiles => BuildSet(new List<PrecomputedTranslationFileReadModel>());

    public DbSet<TranslatorReadModel> Translators => BuildSet(_translators);

    private static DbSet<T> BuildSet<T>(List<T> source) where T : class
    {
        TestAsyncEnumerable<T> queryable = new(source);
        DbSet<T> set = Substitute.For<DbSet<T>, IQueryable<T>, IAsyncEnumerable<T>>();

        ((IQueryable<T>)set).Provider.Returns(queryable.AsQueryable().Provider);
        ((IQueryable<T>)set).Expression.Returns(((IQueryable<T>)queryable).Expression);
        ((IQueryable<T>)set).ElementType.Returns(((IQueryable<T>)queryable).ElementType);
        ((IQueryable<T>)set).GetEnumerator().Returns(_ => source.GetEnumerator());
        ((IAsyncEnumerable<T>)set).GetAsyncEnumerator(Arg.Any<CancellationToken>())
            .Returns(_ => new TestAsyncEnumerator<T>(source.GetEnumerator()));

        return set;
    }
}
