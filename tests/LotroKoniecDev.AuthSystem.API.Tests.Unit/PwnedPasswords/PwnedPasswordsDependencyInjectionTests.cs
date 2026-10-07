using Microsoft.Extensions.DependencyInjection;
using LotroKoniecDev.AuthSystem.Infrastructure.PwnedPasswords;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.PwnedPasswords;

/// <summary>
/// The client exactly as the auth API registers it, with only the network swapped for a stub. The
/// checker's own tests build their client by hand, so the address, the size cap and the handler
/// settings are proven here.
/// </summary>
public sealed class PwnedPasswordsDependencyInjectionTests
{
    [Fact]
    public async Task AddPwnedPasswords_SendsTheCheckToTheDocumentedRangeEndpoint()
    {
        // Arrange
        using StubRangeApiHandler rangeApi = StubRangeApiHandler.Answering(string.Empty);
        await using ServiceProvider provider = BuildProvider(rangeApi);
        IPwnedPasswordChecker checker = provider.GetRequiredService<IPwnedPasswordChecker>();

        // Act
        await checker.CheckAsync("password", CancellationToken.None);

        // Assert
        rangeApi.Requests.ShouldHaveSingleItem().Uri
            .ShouldBe(new Uri("https://api.pwnedpasswords.com/range/5BAA6"));
    }

    [Fact]
    public async Task AddPwnedPasswords_WhenTheAnswerIsLargerThanTheCap_ReturnsUnavailable()
    {
        // Arrange
        string oversizedBody = new('A', checked((int)PwnedPasswordsDependencyInjection.MaxResponseContentBytes + 1));
        using StubRangeApiHandler rangeApi = StubRangeApiHandler.Answering(oversizedBody);
        await using ServiceProvider provider = BuildProvider(rangeApi);
        IPwnedPasswordChecker checker = provider.GetRequiredService<IPwnedPasswordChecker>();

        // Act
        PwnedPasswordVerdict verdict = await checker.CheckAsync("password", CancellationToken.None);

        // Assert
        verdict.ShouldBe(PwnedPasswordVerdict.Unavailable);
    }

    [Fact]
    public void CreatePrimaryHandler_FollowsNoRedirectAndKeepsNoCookies()
    {
        using SocketsHttpHandler handler = PwnedPasswordsDependencyInjection.CreatePrimaryHandler();

        handler.AllowAutoRedirect.ShouldBeFalse();
        handler.UseCookies.ShouldBeFalse();
    }

    private static ServiceProvider BuildProvider(StubRangeApiHandler rangeApi)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddPwnedPasswords();

        // Configured last, so this primary handler replaces the real one and nothing reaches the network.
        services.AddHttpClient(nameof(IPwnedPasswordChecker))
            .ConfigurePrimaryHttpMessageHandler(() => rangeApi);

        return services.BuildServiceProvider();
    }
}
