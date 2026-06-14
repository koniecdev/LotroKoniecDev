using LotroKoniecDev.SharedKernel.Guards;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.Repositories;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.WriteDbContexts;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;
using Microsoft.EntityFrameworkCore;

namespace LotroKoniecDev.TranslationSystem.Persistence.DomainRepositories;

internal sealed class TranslatorRepository : GenericRepository<Translator, TranslatorId>, ITranslatorRepository
{
    public TranslatorRepository(ApplicationWriteDbContext db) : base(db)
    {
    }

    public async Task<Maybe<Translator>> GetByIdentityIdAsync(IdentityId identityId, CancellationToken cancellationToken)
    {
        Ensure.NotEmpty(identityId);

        Translator? translator = await DbContext.Translators
            .FirstOrDefaultAsync(row => row.IdentityId == identityId, cancellationToken);

        return Maybe<Translator>.From(translator);
    }

    public void Detach(Translator translator)
    {
        ArgumentNullException.ThrowIfNull(translator);

        DbContext.Entry(translator).State = EntityState.Detached;
    }
}
