using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LotroKoniecDev.Tests.Shared;

/// <summary>
/// Starts a <c>WebApplicationFactory</c> host so that a refused start always ends in the app's own error
/// (#1040). The app starts on a thread of its own, and when its start fails, that thread disposes the
/// host. The test host reads the host's services before it waits for the start, so a test thread that
/// came late got an <see cref="ObjectDisposedException"/> instead of the app's error. Here the app waits
/// with its start until the test host has read them. Linked into the three in-memory host suites.
/// </summary>
internal static class HostStartOrder
{
    public static IHost BuildAndStart(IHostBuilder builder)
    {
        TaskCompletionSource testHostIsWaiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        builder.ConfigureServices(services => HoldTheStartUntil(services, testHostIsWaiting.Task));

        IHost host;
        Task started;
        try
        {
            host = builder.Build();

            // StartAsync reads the host's services before it first waits, so the app may go on once it returns.
            started = host.StartAsync();
        }
        finally
        {
            testHostIsWaiting.TrySetResult();
        }

        started.GetAwaiter().GetResult();
        return host;
    }

    private static void HoldTheStartUntil(IServiceCollection services, Task testHostIsWaiting)
    {
        ServiceDescriptor appLifetime = services.Last(descriptor => descriptor.ServiceType == typeof(IHostLifetime));
        Type appLifetimeType = appLifetime.ImplementationType
            ?? throw new InvalidOperationException("The host lifetime is not registered by its type, so its start cannot be held.");

        services.Remove(appLifetime);
        services.AddSingleton(appLifetimeType);
        services.AddSingleton<IHostLifetime>(provider =>
            new HeldHostLifetime((IHostLifetime)provider.GetRequiredService(appLifetimeType), testHostIsWaiting));
    }

    private sealed class HeldHostLifetime : IHostLifetime
    {
        private readonly IHostLifetime _appLifetime;
        private readonly Task _testHostIsWaiting;

        public async Task WaitForStartAsync(CancellationToken cancellationToken)
        {
            await _testHostIsWaiting.WaitAsync(cancellationToken);
            await _appLifetime.WaitForStartAsync(cancellationToken);
        }

        public Task StopAsync(CancellationToken cancellationToken) => _appLifetime.StopAsync(cancellationToken);

        public HeldHostLifetime(IHostLifetime appLifetime, Task testHostIsWaiting)
        {
            _appLifetime = appLifetime;
            _testHostIsWaiting = testHostIsWaiting;
        }
    }
}
