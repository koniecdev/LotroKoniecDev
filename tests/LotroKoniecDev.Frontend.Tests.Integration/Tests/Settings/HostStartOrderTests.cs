using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using LotroKoniecDev.Tests.Shared;

namespace LotroKoniecDev.Frontend.Tests.Integration.Tests.Settings;

/// <summary>
/// #1040: a refused start must end in the app's own error, even when the test thread comes late. A busy
/// full run made it late only sometimes, so this test makes it late every time. Without the hold in
/// <see cref="HostStartOrder"/>, the app's thread disposes the host first and this test gets an
/// <see cref="ObjectDisposedException"/>.
/// </summary>
public sealed class HostStartOrderTests
{
    [Fact]
    public void Boot_WithAMissingCallerKeyAndALateTestThread_ShouldFailNamingTheKey()
    {
        // Arrange
        using LateTestThreadFactory factory = new();
        using WebApplicationFactory<Program> badKeyHost = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "AuthSystem:CallerKey", null }
                });
            });
        });

        // Act
        OptionsValidationException exception = Should.Throw<OptionsValidationException>(() => badKeyHost.CreateClient());

        // Assert
        exception.Message.ShouldContain("AuthSystem:CallerKey", Case.Sensitive);
    }

    private sealed class LateTestThreadFactory : ProductionFrontendFactory
    {
        protected override IHost CreateHost(IHostBuilder builder) =>
            HostStartOrder.BuildAndStart(new LateHostBuilder(builder));
    }

    /// <summary>
    /// Hands the built host back late, so the app's thread has the time to refuse its start before the
    /// test thread goes on.
    /// </summary>
    private sealed class LateHostBuilder : IHostBuilder
    {
        private static readonly TimeSpan Delay = TimeSpan.FromSeconds(1);

        private readonly IHostBuilder _builder;

        public IDictionary<object, object> Properties => _builder.Properties;

        public IHost Build()
        {
            IHost host = _builder.Build();
            Thread.Sleep(Delay);
            return host;
        }

        public IHostBuilder ConfigureHostConfiguration(Action<IConfigurationBuilder> configureDelegate)
        {
            _builder.ConfigureHostConfiguration(configureDelegate);
            return this;
        }

        public IHostBuilder ConfigureAppConfiguration(Action<HostBuilderContext, IConfigurationBuilder> configureDelegate)
        {
            _builder.ConfigureAppConfiguration(configureDelegate);
            return this;
        }

        public IHostBuilder ConfigureServices(Action<HostBuilderContext, IServiceCollection> configureDelegate)
        {
            _builder.ConfigureServices(configureDelegate);
            return this;
        }

        public IHostBuilder UseServiceProviderFactory<TContainerBuilder>(IServiceProviderFactory<TContainerBuilder> factory)
            where TContainerBuilder : notnull
        {
            _builder.UseServiceProviderFactory(factory);
            return this;
        }

        public IHostBuilder UseServiceProviderFactory<TContainerBuilder>(
            Func<HostBuilderContext, IServiceProviderFactory<TContainerBuilder>> factory)
            where TContainerBuilder : notnull
        {
            _builder.UseServiceProviderFactory(factory);
            return this;
        }

        public IHostBuilder ConfigureContainer<TContainerBuilder>(Action<HostBuilderContext, TContainerBuilder> configureDelegate)
        {
            _builder.ConfigureContainer(configureDelegate);
            return this;
        }

        public LateHostBuilder(IHostBuilder builder)
        {
            _builder = builder;
        }
    }
}
