using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace WinForward.Analyzers;

/// <summary>
/// WF0002 — <c>Task.ContinueWith</c>. Keyed on the resolved method symbol, so a user-defined
/// <c>ContinueWith</c> is not flagged. <c>Task&lt;TResult&gt;</c> declares its own <c>ContinueWith</c>
/// overloads (which member lookup prefers over the inherited <c>Task</c> ones), so both containing
/// types are matched on their original definitions.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ContinueWithAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [DiagnosticDescriptors.ContinueWith];

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
                    if (string.Equals(method.Name, "ContinueWith", StringComparison.Ordinal)
                        && IsTaskOrTaskOfT(method.ContainingType, types))
                    {
                        operationContext.ReportDiagnostic(Diagnostic.Create(
                            DiagnosticDescriptors.ContinueWith,
                            operationContext.Operation.Syntax.GetLocation()));
                    }
                },
                OperationKind.Invocation);
        });
    }

    private static bool IsTaskOrTaskOfT(INamedTypeSymbol? containingType, WellKnownTypes types)
    {
        var definition = containingType?.OriginalDefinition;
        return SymbolEqualityComparer.Default.Equals(definition, types.Task)
            || SymbolEqualityComparer.Default.Equals(definition, types.TaskOfT);
    }
}
