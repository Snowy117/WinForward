using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace WinForward.Analyzers;

/// <summary>
/// WF0004 — a bare unawaited awaitable expression statement (<c>FooAsync();</c>). The assignment
/// exclusion keeps <c>_ = FooAsync();</c> from being reported twice (WF0001 owns it) and keeps
/// <c>field = FooAsync();</c> / <c>field ??= FooAsync();</c> from being false positives, since
/// storing an awaitable for a later consumer is legitimate. The await exclusion keeps
/// <c>await Task.WhenAny(first, second);</c> quiet: the awaited expression's result happens to be
/// awaitable, but nothing is left unobserved.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class BareAwaitableExpressionAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [DiagnosticDescriptors.BareAwaitableExpression];

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
                    var statement = (IExpressionStatementOperation)operationContext.Operation;
                    if (statement.Operation is IAssignmentOperation or IAwaitOperation)
                    {
                        return;
                    }

                    if (AwaitableClassifier.IsAwaitable(statement.Operation.Type, types))
                    {
                        operationContext.ReportDiagnostic(Diagnostic.Create(
                            DiagnosticDescriptors.BareAwaitableExpression,
                            statement.Syntax.GetLocation()));
                    }
                },
                OperationKind.ExpressionStatement);
        });
    }
}
