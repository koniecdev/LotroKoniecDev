using LotroKoniecDev.Frontend.Infrastructure.Auth.DeadSession;
using LotroKoniecDev.Frontend.Tests.Unit.Shared;

namespace LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.Auth.DeadSession;

public sealed class DeadSessionRegistryTests
{
    private const string AccessToken = "laptop-access-token";

    [Fact]
    public async Task ConsumeAsync_WhenNeverMarked_ReturnsFalse()
    {
        DeadSessionRegistry registry = DeadSessionRegistryFactory.Create();

        bool isDead = await registry.ConsumeAsync(AccessToken);

        isDead.ShouldBeFalse();
    }

    [Fact]
    public async Task ConsumeAsync_AfterMarkDead_ReturnsTrue()
    {
        DeadSessionRegistry registry = DeadSessionRegistryFactory.Create();

        await registry.MarkDeadAsync(AccessToken);
        bool isDead = await registry.ConsumeAsync(AccessToken);

        isDead.ShouldBeTrue();
    }

    [Fact]
    public async Task ConsumeAsync_IsOneShot_SecondCallReturnsFalse()
    {
        DeadSessionRegistry registry = DeadSessionRegistryFactory.Create();

        await registry.MarkDeadAsync(AccessToken);
        bool first = await registry.ConsumeAsync(AccessToken);
        bool second = await registry.ConsumeAsync(AccessToken);

        first.ShouldBeTrue();
        second.ShouldBeFalse();
    }

    /// <summary>
    /// #1061: the other token is the same user's phone.
    /// </summary>
    [Fact]
    public async Task ConsumeAsync_ForAnotherSessionsToken_ReturnsFalse()
    {
        DeadSessionRegistry registry = DeadSessionRegistryFactory.Create();

        await registry.MarkDeadAsync(AccessToken);
        bool isDead = await registry.ConsumeAsync("phone-access-token");

        isDead.ShouldBeFalse();
    }

    /// <summary>
    /// #1061: the phone reading its own marker must not delete the laptop's.
    /// </summary>
    [Fact]
    public async Task ConsumeAsync_ForAnotherSessionsToken_KeepsTheMarkedSessionsMarker()
    {
        DeadSessionRegistry registry = DeadSessionRegistryFactory.Create();

        await registry.MarkDeadAsync(AccessToken);
        await registry.ConsumeAsync("phone-access-token");
        bool isDead = await registry.ConsumeAsync(AccessToken);

        isDead.ShouldBeTrue();
    }

    /// <summary>
    /// A real access token is longer than the cache's 256-character key limit, so the marker works only
    /// because the key is a hash.
    /// </summary>
    [Fact]
    public async Task ConsumeAsync_AfterMarkDeadWithATokenLongerThanTheCacheKeyLimit_ReturnsTrue()
    {
        DeadSessionRegistry registry = DeadSessionRegistryFactory.Create();
        string longAccessToken = new('a', 2048);

        await registry.MarkDeadAsync(longAccessToken);
        bool isDead = await registry.ConsumeAsync(longAccessToken);

        isDead.ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ConsumeAsync_AfterMarkDeadWithABlankToken_ReturnsFalse(string blankAccessToken)
    {
        DeadSessionRegistry registry = DeadSessionRegistryFactory.Create();

        await registry.MarkDeadAsync(blankAccessToken);
        bool isDead = await registry.ConsumeAsync(blankAccessToken);

        isDead.ShouldBeFalse();
    }
}
