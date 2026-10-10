using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LotroKoniecDev.AuthSystem.Infrastructure.PwnedPasswords;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.PwnedPasswords;

/// <summary>
/// The client exactly as the auth API registers it, with only the network swapped for a stub. The
/// checker's own tests build their client by hand, so the address, the time limit, the size cap, the
/// handler settings and the silent logging are proven here.
/// </summary>
public sealed class PwnedPasswordsDependencyInjectionTests
{
    private const string Password = "password";
    private const string HashPrefix = "5BAA6";

    [Fact]
    public async Task AddPwnedPasswords_WhenTheCheckRuns_SendsItToTheDocumentedRangeEndpoint()
    {
        // Arrange
        using StubRangeApiHandler rangeApi = StubRangeApiHandler.Answering("0018A45C4D1DEF81644B54AB7F969B88D65:1");
        await using ServiceProvider provider = BuildProvider(rangeApi, new CapturingLoggerProvider());
        IPwnedPasswordChecker checker = provider.GetRequiredService<IPwnedPasswordChecker>();

        // Act
        await checker.CheckAsync(Password, CancellationToken.None);

        // Assert
        rangeApi.Requests.ShouldHaveSingleItem().Uri
            .ShouldBe(new Uri("https://api.pwnedpasswords.com/range/" + HashPrefix));
    }

    /// <summary>
    /// The prefix is safe at Have I Been Pwned, which cannot tie it to anyone. In our own log it would sit
    /// next to the request and the account it belongs to (ADR-0066).
    /// </summary>
    [Fact]
    public async Task AddPwnedPasswords_WhenTheCheckRuns_LogsNothingThatCarriesTheHashPrefix()
    {
        // Arrange
        using StubRangeApiHandler rangeApi = StubRangeApiHandler.Answering("0018A45C4D1DEF81644B54AB7F969B88D65:1");
        CapturingLoggerProvider logs = new();
        await using ServiceProvider provider = BuildProvider(rangeApi, logs);
        IPwnedPasswordChecker checker = provider.GetRequiredService<IPwnedPasswordChecker>();

        // Act
        await checker.CheckAsync(Password, CancellationToken.None);

        // Assert
        logs.Messages.ShouldNotContain(message => message.Contains(HashPrefix, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AddPwnedPasswords_WhenTheAnswerIsLargerThanTheCap_ReturnsUnavailable()
    {
        // Arrange
        string oversizedBody = new('A', checked((int)PwnedPasswordsDependencyInjection.MaxResponseContentBytes + 1));
        using StubRangeApiHandler rangeApi = StubRangeApiHandler.Answering(oversizedBody);
        await using ServiceProvider provider = BuildProvider(rangeApi, new CapturingLoggerProvider());
        IPwnedPasswordChecker checker = provider.GetRequiredService<IPwnedPasswordChecker>();

        // Act
        PwnedPasswordVerdict verdict = await checker.CheckAsync(Password, CancellationToken.None);

        // Assert
        verdict.ShouldBe(PwnedPasswordVerdict.Unavailable);
    }

    [Fact]
    public async Task AddPwnedPasswords_WhenTheClientIsBuilt_GivesItTheShortTimeLimit()
    {
        // Arrange
        using StubRangeApiHandler rangeApi = StubRangeApiHandler.Answering(string.Empty);
        await using ServiceProvider provider = BuildProvider(rangeApi, new CapturingLoggerProvider());
        IHttpClientFactory httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();

        // Act
        using HttpClient client = httpClientFactory.CreateClient(nameof(IPwnedPasswordChecker));

        // Assert
        client.Timeout.ShouldBe(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void CreatePrimaryHandler_WhenCreated_FollowsNoRedirectAndKeepsNoCookies()
    {
        using SocketsHttpHandler handler = PwnedPasswordsDependencyInjection.CreatePrimaryHandler();

        handler.AllowAutoRedirect.ShouldBeFalse();
        handler.UseCookies.ShouldBeFalse();
    }

    [Fact]
    public void CreatePrimaryHandler_WhenCreated_PropagatesNoTraceContext()
    {
        using SocketsHttpHandler handler = PwnedPasswordsDependencyInjection.CreatePrimaryHandler();

        handler.ActivityHeadersPropagator.ShouldBeNull();
    }

    private static ServiceProvider BuildProvider(StubRangeApiHandler rangeApi, CapturingLoggerProvider logs)
    {
        ServiceCollection services = new();
        services.AddLogging(logging => logging
            .SetMinimumLevel(LogLevel.Trace)
            .AddProvider(logs));
        services.AddPwnedPasswords();

        // Configured last, so this primary handler replaces the real one and nothing reaches the network.
        services.AddHttpClient(nameof(IPwnedPasswordChecker))
            .ConfigurePrimaryHttpMessageHandler(() => rangeApi);

        return services.BuildServiceProvider();
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _messages = [];
        private readonly Lock _gate = new();

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_gate)
                {
                    return [.. _messages];
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

        public void Dispose()
        {
        }

        private void Add(string message)
        {
            lock (_gate)
            {
                _messages.Add(message);
            }
        }

        private sealed class CapturingLogger : ILogger
        {
            private readonly CapturingLoggerProvider _provider;
            private readonly string _categoryName;

            public CapturingLogger(CapturingLoggerProvider provider, string categoryName)
            {
                _provider = provider;
                _categoryName = categoryName;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                ArgumentNullException.ThrowIfNull(formatter);
                _provider.Add($"{_categoryName}: {formatter(state, exception)} {state}");
            }
        }
    }
}
