using LotroKoniecDev.Frontend.Infrastructure.Auth.DeadSession;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.Auth.DeadSession;

public sealed class DeadSessionRegistryTests
{
    private const string Subject = "11111111-1111-1111-1111-111111111111";

    [Fact]
    public async Task ConsumeAsync_WhenNeverMarked_ReturnsFalse()
    {
        DeadSessionRegistry registry = CreateRegistry();

        bool isDead = await registry.ConsumeAsync(Subject);

        isDead.ShouldBeFalse();
    }

    [Fact]
    public async Task ConsumeAsync_AfterMarkDead_ReturnsTrue()
    {
        DeadSessionRegistry registry = CreateRegistry();

        await registry.MarkDeadAsync(Subject);
        bool isDead = await registry.ConsumeAsync(Subject);

        isDead.ShouldBeTrue();
    }

    [Fact]
    public async Task ConsumeAsync_IsOneShot_SecondCallReturnsFalse()
    {
        DeadSessionRegistry registry = CreateRegistry();

        await registry.MarkDeadAsync(Subject);
        bool first = await registry.ConsumeAsync(Subject);
        bool second = await registry.ConsumeAsync(Subject);

        first.ShouldBeTrue();
        second.ShouldBeFalse();
    }

    [Fact]
    public async Task ConsumeAsync_ForADifferentSubject_ReturnsFalse()
    {
        DeadSessionRegistry registry = CreateRegistry();

        await registry.MarkDeadAsync(Subject);
        bool isDead = await registry.ConsumeAsync("22222222-2222-2222-2222-222222222222");

        isDead.ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MarkDeadAsync_WithBlankSubject_IsNoOp(string blankSubject)
    {
        DeadSessionRegistry registry = CreateRegistry();

        await registry.MarkDeadAsync(blankSubject);

        // Nothing was stored, so a subsequent consume for the same blank key finds no marker.
        bool isDead = await registry.ConsumeAsync(blankSubject);
        isDead.ShouldBeFalse();
    }

    private static DeadSessionRegistry CreateRegistry()
    {
        ServiceCollection services = new();
        services.AddHybridCache();
        ServiceProvider provider = services.BuildServiceProvider();
        HybridCache hybridCache = provider.GetRequiredService<HybridCache>();
        return new DeadSessionRegistry(hybridCache);
    }
}
