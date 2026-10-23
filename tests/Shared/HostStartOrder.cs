using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LotroKoniecDev.Tests.Shared;

/// <summary>
/// Makes a refused start end in the app's own error, not in an <see cref="ObjectDisposedException"/>
/// (#1040). The app starts on its own thread, and when its <c>RunAsync</c> fails, that thread disposes
/// the host while the test host may still be reading its services. So the app holds its start until the
/// test host has read them. A refusal before <c>RunAsync</c> (a seed step) needs no hold, but an
/// <c>await using</c> on the app in a Program.cs would change that. This replaces the default
/// <c>CreateHost</c>, which also sets the port of a <c>UseKestrel(port)</c> host; no suite uses that.
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
        ServiceDescriptor appLifetime = services.Last(descriptor =>
            descriptor.ServiceType == typeof(IHostLifetime) && !descriptor.IsKeyedService);
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
            // No token: a cancelled start must wait too, or the host could be gone before the test host reads it.
            await _testHostIsWaiting.ConfigureAwait(false);
            await _appLifetime.WaitForStartAsync(cancellationToken).ConfigureAwait(false);
        }

        public Task StopAsync(CancellationToken cancellationToken) => _appLifetime.StopAsync(cancellationToken);

        public HeldHostLifetime(IHostLifetime appLifetime, Task testHostIsWaiting)
        {
            _appLifetime = appLifetime;
            _testHostIsWaiting = testHostIsWaiting;
        }
    }
}
