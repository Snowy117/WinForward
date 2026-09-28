using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;

namespace WinForward.Analyzers.Tests;

/// <summary>
/// Shared harness for the WF0001–WF0004 rules. Snippets compile against the .NET 9 reference
/// assemblies, which provide every task type the rules key on plus the custom-awaitable shape.
/// Only <see cref="CompilerDiagnostics.Errors"/> are verified, so CS4014 — a warning — does not have
/// to be declared in the WF0004 snippets.
/// </summary>
internal abstract class LifetimeAnalyzerTest<TAnalyzer> : CSharpAnalyzerTest<TAnalyzer, DefaultVerifier>
    where TAnalyzer : DiagnosticAnalyzer, new()
{
    protected LifetimeAnalyzerTest()
    {
        ReferenceAssemblies = ReferenceAssemblies.Net.Net90;
        CompilerDiagnostics = CompilerDiagnostics.Errors;
    }
}
