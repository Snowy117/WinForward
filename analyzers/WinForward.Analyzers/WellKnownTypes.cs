using Microsoft.CodeAnalysis;

namespace WinForward.Analyzers;

/// <summary>
/// The task types the WF rules key on, resolved once per compilation through
/// <see cref="Compilation.GetTypeByMetadataName(string)"/> rather than once per syntax node.
/// </summary>
internal sealed class WellKnownTypes
{
    private WellKnownTypes(Compilation compilation)
    {
        Task = compilation.GetTypeByMetadataName("System.Threading.Tasks.Task");
        TaskOfT = compilation.GetTypeByMetadataName("System.Threading.Tasks.Task`1");
        ValueTask = compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask");
        ValueTaskOfT = compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask`1");
        TaskFactory = compilation.GetTypeByMetadataName("System.Threading.Tasks.TaskFactory");
        TaskFactoryOfT = compilation.GetTypeByMetadataName("System.Threading.Tasks.TaskFactory`1");
    }

    public INamedTypeSymbol? Task { get; }

    public INamedTypeSymbol? TaskOfT { get; }

    public INamedTypeSymbol? ValueTask { get; }

    public INamedTypeSymbol? ValueTaskOfT { get; }

    public INamedTypeSymbol? TaskFactory { get; }

    public INamedTypeSymbol? TaskFactoryOfT { get; }

    public static WellKnownTypes Create(Compilation compilation) => new(compilation);
}
