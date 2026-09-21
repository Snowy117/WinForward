using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace WinForward.Analyzers;

/// <summary>
/// WF0001 — an unawaited awaitable is discarded (<c>_ = &lt;awaitable&gt;</c>). The discard is
/// matched on the operation, so the embedded form <c>if (cond) _ = FooAsync();</c> is covered too.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UnawaitedAwaitableDiscardAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [DiagnosticDescriptors.UnawaitedAwaitableDiscard];

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
                    var assignment = (ISimpleAssignmentOperation)operationContext.Operation;
                    if (assignment.Target is not IDiscardOperation)
                    {
                        return;
                    }

                    if (AwaitableClassifier.IsAwaitable(assignment.Value.Type, types))
                    {
                        operationContext.ReportDiagnostic(Diagnostic.Create(
                            DiagnosticDescriptors.UnawaitedAwaitableDiscard,
                            assignment.Syntax.GetLocation()));
                    }
                },
                OperationKind.SimpleAssignment);
        });
    }
}
