using LotroKoniecDev.SharedKernel.StronglyTypedIds.Common;

namespace LotroKoniecDev.TranslationSystem.ReadModels.Core.BuildingBlocks;

public interface IReadOnlyEntity<out T> where T : struct, IStronglyTypedId<T>
{
    public T Id { get; }
    public DateTimeOffset CreatedAt { get; }
}
