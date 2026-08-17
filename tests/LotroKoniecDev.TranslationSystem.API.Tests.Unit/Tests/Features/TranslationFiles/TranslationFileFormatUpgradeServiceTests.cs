using LotroKoniecDev.TranslationSystem.API.Features.TranslationFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Features.TranslationFiles;

/// <summary>
/// The catch in the startup regeneration is load-bearing: an exception escaping a
/// <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> stops the whole host, and a stale
/// artifact is a degraded patch, not a dead API (ADR-0047 Consequences — "Deploy ordering").
/// </summary>
public sealed class TranslationFileFormatUpgradeServiceTests
{
    [Fact]
    public async Task ExecuteAsync_WhenTheUpgradeThrows_ShouldSwallowItAndLeaveTheHostRunning()
    {
        // Arrange — the very first thing the upgrade does is open a scope; make that blow up.
        TranslationFileFormatUpgradeService service = new(
            new ThrowingScopeFactory(() => new InvalidOperationException("database unreachable")),
            new UntouchedProjector(),
            NullLogger<TranslationFileFormatUpgradeService>.Instance);

        // Act
        await service.StartAsync(CancellationToken.None);
        Func<Task> execution = async () => await service.ExecuteTask!;

        // Assert — the failure is logged, not thrown; nothing was regenerated.
        await execution.ShouldNotThrowAsync();
        await service.StopAsync(CancellationToken.None);
    }

    private sealed class ThrowingScopeFactory : IServiceScopeFactory
    {
        private readonly Func<Exception> _failure;

        public ThrowingScopeFactory(Func<Exception> failure)
        {
            _failure = failure;
        }

        public IServiceScope CreateScope() => throw _failure();
    }

    private sealed class UntouchedProjector : IPrecomputedTranslationFileProjector
    {
        public Task RebuildAsync(string language, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Nothing should be regenerated in this test.");
    }
}
