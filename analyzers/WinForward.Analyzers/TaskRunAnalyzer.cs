using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace WinForward.Analyzers;

/// <summary>
/// WF0003 — <c>Task.Run</c>, <c>Task.Factory.StartNew</c> and <c>TaskFactory&lt;T&gt;.StartNew</c>.
/// Keyed on the resolved containing type, so user-defined <c>Run</c>/<c>StartNew</c> are not flagged.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class TaskRunAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [DiagnosticDescriptors.TaskRun];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(static compilationStartContext =>
        {
            var types = WellKnownTypes.Create(compilationStartContext.Compilation);
            compilationStartContext.RegisterOperationAction(
                operationContext =>
                {
                    var method = ((IInvocationOperation)operationContext.Operation).TargetMethod;
                    if (method.Name is "Run" or "StartNew" && IsTaskOrFactory(method.ContainingType, types))
                    {
                        operationContext.ReportDiagnostic(Diagnostic.Create(
                            DiagnosticDescriptors.TaskRun,
                            operationContext.Operation.Syntax.GetLocation()));
                    }
                },
                OperationKind.Invocation);
        });
    }

    private static bool IsTaskOrFactory(INamedTypeSymbol? containingType, WellKnownTypes types)
    {
        var definition = containingType?.OriginalDefinition;
        return SymbolEqualityComparer.Default.Equals(definition, types.Task)
            || SymbolEqualityComparer.Default.Equals(definition, types.TaskFactory)
            || SymbolEqualityComparer.Default.Equals(definition, types.TaskFactoryOfT);
    }
}
