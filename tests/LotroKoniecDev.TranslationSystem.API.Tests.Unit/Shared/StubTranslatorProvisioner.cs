using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.API.Auth.Provisioning;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Shared;

/// <summary>
/// Hand-written double for the internal <see cref="ITranslatorProvisioner"/> (NSubstitute can't
/// proxy internal interfaces here): returns a fixed provisioning result and records how many times
/// it was invoked.
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
