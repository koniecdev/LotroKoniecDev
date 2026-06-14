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
/// A pure in-memory <see cref="IApplicationReadDbContext"/> double for unit-testing the write
/// handlers' read-back projection: only the <see cref="Translations"/> set is populated (the only
/// one the write handlers read). EF Core's async operators run via <see cref="TestAsyncQueryProvider{T}"/>.
/// </summary>
internal sealed class FakeReadDbContext : IApplicationReadDbContext
{
    private readonly List<TranslationReadModel> _translations;

    public FakeReadDbContext(IEnumerable<TranslationReadModel> translations)
    {
        _translations = [.. translations];
    }

    public DbSet<GameVersionReadModel> GameVersions => BuildSet(new List<GameVersionReadModel>());

    public DbSet<TranslationReadModel> Translations => BuildSet(_translations);

    public DbSet<PrecomputedTranslationFileReadModel> PrecomputedTranslationFiles => BuildSet(new List<PrecomputedTranslationFileReadModel>());

    public DbSet<TranslatorReadModel> Translators => BuildSet(new List<TranslatorReadModel>());

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
