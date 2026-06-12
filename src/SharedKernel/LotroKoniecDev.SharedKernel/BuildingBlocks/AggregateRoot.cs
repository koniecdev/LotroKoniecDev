namespace LotroKoniecDev.SharedKernel.BuildingBlocks;

public interface IAggregateRoot;

public abstract class AggregateRoot<TId> : Entity<TId>, IAggregateRoot where TId : struct
{
    protected AggregateRoot(TId id) : base(id)
    {
    }

    protected AggregateRoot()
    {
    }
}
