using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.API.Auth.Provisioning;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Shared;

/// <summary>
/// NSubstitute cannot proxy the internal <see cref="ITranslatorProvisioner"/>.
/// </summary>
internal sealed class StubTranslatorProvisioner : ITranslatorProvisioner
{
    private readonly Result<TranslatorId> _result;

    public StubTranslatorProvisioner(Result<TranslatorId> result)
    {
        _result = result;
    }

    public int CallCount { get; private set; }

    public ValueTask<Result<TranslatorId>> ProvisionCurrentAsync(CancellationToken cancellationToken)
    {
        CallCount++;
        return ValueTask.FromResult(_result);
    }
}
