using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.SharedKernel.BuildingBlocks;

public interface IRepository<TAggregateRoot, in TAggregateRootId>
    where TAggregateRootId : struct
    where TAggregateRoot : AggregateRoot<TAggregateRootId>
{
    Task<Maybe<TAggregateRoot>> GetByIdAsync(TAggregateRootId id, CancellationToken cancellationToken);
    Task<bool> ExistsAsync(TAggregateRootId id, CancellationToken cancellationToken);
    void Insert(TAggregateRoot aggregate);
    void Remove(TAggregateRoot aggregate);
}
