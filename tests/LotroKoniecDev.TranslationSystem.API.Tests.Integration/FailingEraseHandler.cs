using LotroKoniecDev.SharedKernel.Messaging;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.API.Features.Translators;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration;

/// <summary>
/// The real erase handler, behind a switch that makes it fail first, like a database that is down.
/// </summary>
internal sealed class FailingEraseHandler : ICommandHandler<EraseTranslatorProfile.Command, Result>
{
    private readonly ICommandHandler<EraseTranslatorProfile.Command, Result> _inner;
    private readonly ErasureFailures _failures;

    public FailingEraseHandler(
        ICommandHandler<EraseTranslatorProfile.Command, Result> inner,
        ErasureFailures failures)
    {
        _inner = inner;
        _failures = failures;
    }

    public ValueTask<Result> Handle(EraseTranslatorProfile.Command command, CancellationToken cancellationToken)
    {
        return _failures.TakeOne() is { } failure
            ? throw failure
            : _inner.Handle(command, cancellationToken);
    }
}
