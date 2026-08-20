using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;

namespace LotroKoniecDev.Cli.DependencyInjection;

public sealed class TypeResolver : ITypeResolver, IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly IServiceScope _scope;

    public TypeResolver(ServiceProvider provider)
    {
        _provider = provider;
        // One scope for the whole process. The CLI runs exactly one command per run, so the scope
        // lives as long as that command. Resolving from the root provider would throw once
        // ValidateScopes is on, because the commands depend on scoped handlers.
        _scope = provider.CreateScope();
    }

    public object? Resolve(Type? type)
    {
        object? result = type is null ? null : _scope.ServiceProvider.GetService(type);
        return result;
    }

    public void Dispose()
    {
        _scope.Dispose();
        _provider.Dispose();
    }
}
