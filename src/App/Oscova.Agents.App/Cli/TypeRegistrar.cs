using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;

namespace Oscova.Agents.App.Cli;

/// <summary>
/// Bridges Spectre.Console.Cli command creation to Microsoft dependency injection.
/// </summary>
public sealed class TypeRegistrar(IServiceCollection services) : ITypeRegistrar
{
    public void Register(Type service, Type implementation) => services.AddSingleton(service, implementation);
    public void RegisterInstance(Type service, object implementation) => services.AddSingleton(service, implementation);
    public void RegisterLazy(Type service, Func<object> factory) => services.AddSingleton(service, _ => factory());
    public ITypeResolver Build() => new TypeResolver(services.BuildServiceProvider());
}

internal sealed class TypeResolver(IServiceProvider provider) : ITypeResolver
{
    public object? Resolve(Type? type) => type is null ? null : provider.GetService(type);
}