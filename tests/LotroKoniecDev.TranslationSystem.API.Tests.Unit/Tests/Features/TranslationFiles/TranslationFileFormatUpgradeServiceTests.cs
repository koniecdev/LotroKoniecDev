using LotroKoniecDev.TranslationSystem.API.Features.TranslationFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Features.TranslationFiles;

/// <summary>
/// Pins the catch: an exception leaving a <see cref="Microsoft.Extensions.Hosting.BackgroundService"/>
/// stops the host, while an old artifact only means a worse patch (ADR-0047, "Deploy ordering").
/// </summary>
public sealed class TranslationFileFormatUpgradeServiceTests
{
    [Fact]
    public async Task ExecuteAsync_WhenTheUpgradeThrows_ShouldSwallowItAndLeaveTheHostRunning()
    {
        // Arrange: opening the scope, the upgrade's first step, fails.
        TranslationFileFormatUpgradeService service = new(
            new ThrowingScopeFactory(() => new InvalidOperationException("database unreachable")),
            new UntouchedProjector(),
            NullLogger<TranslationFileFormatUpgradeService>.Instance);

        // Act
        await service.StartAsync(CancellationToken.None);
        Func<Task> execution = async () => await service.ExecuteTask!;

        // Assert
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
