namespace LotroKoniecDev.Application.Abstractions.Messaging;

public interface IQueryHandler<in TQuery, TResponse>
    where TQuery : IQuery<TResponse>
{
    ValueTask<TResponse> Handle(TQuery query, CancellationToken cancellationToken);
}
