using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Guards;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.SharedKernel.StronglyTypedIds.Common;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.WriteDbContexts;
using Microsoft.EntityFrameworkCore;

namespace LotroKoniecDev.TranslationSystem.Persistence;

internal abstract class GenericRepository<TAggregateRoot, TAggregateRootId> : IRepository<TAggregateRoot, TAggregateRootId>
    where TAggregateRootId : struct, IStronglyTypedId<TAggregateRootId>
    where TAggregateRoot : AggregateRoot<TAggregateRootId>
{
    protected readonly ApplicationWriteDbContext DbContext;

    protected GenericRepository(ApplicationWriteDbContext db)
    {
        DbContext = db;
    }

    public virtual async Task<Maybe<TAggregateRoot>> GetByIdAsync(
        TAggregateRootId id,
        CancellationToken cancellationToken)
    {
        Ensure.NotEmpty(id);

        TAggregateRoot? entity = await DbContext.Set<TAggregateRoot>()
            .FirstOrDefaultAsync(x => x.Id.Equals(id), cancellationToken);

        return Maybe<TAggregateRoot>.From(entity);
    }

    public async Task<bool> ExistsAsync(TAggregateRootId id, CancellationToken cancellationToken)
    {
        Ensure.NotEmpty(id);

        bool result = await DbContext.Set<TAggregateRoot>()
            .AnyAsync(x => x.Id.Equals(id), cancellationToken);

        return result;
    }

    public void Insert(TAggregateRoot aggregate)
    {
        ArgumentNullException.ThrowIfNull(aggregate);

        DbContext.Set<TAggregateRoot>().Add(aggregate);
    }

    public void Remove(TAggregateRoot aggregate)
    {
        ArgumentNullException.ThrowIfNull(aggregate);

        DbContext.Set<TAggregateRoot>().Remove(aggregate);
    }
}
