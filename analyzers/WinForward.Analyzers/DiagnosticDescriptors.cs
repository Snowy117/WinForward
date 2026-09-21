using Microsoft.CodeAnalysis;

namespace WinForward.Analyzers;

/// <summary>
/// The WF0001–WF0004 descriptors. These rules make the fire-and-forget escape syntaxes a build error
/// in <c>src/**</c>; the one sanctioned alternative is
/// <c>WinForward.Runtime.QuiescenceScope.Run</c>.
/// </summary>
internal static class DiagnosticDescriptors
{
    private const string Category = "WinForward.Lifetime";

    public static readonly DiagnosticDescriptor UnawaitedAwaitableDiscard = new(
        id: "WF0001",
        title: "Do not discard an unawaited awaitable",
        messageFormat: "Await this awaitable or start it as a tracked child with QuiescenceScope.Run",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Discarding an unawaited awaitable detaches the work from its owner: the work can outlive the owner's disposal, its failure is never observed, and its ordering with respect to shutdown is unspecified.");

    public static readonly DiagnosticDescriptor ContinueWith = new(
        id: "WF0002",
        title: "Do not use Task.ContinueWith",
        messageFormat: "Record the fault inside the child body instead of using Task.ContinueWith",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A ContinueWith continuation is a patch for a task someone may abandon. Observing the fault inside the child body makes an abandoned child safe by construction.");

    public static readonly DiagnosticDescriptor TaskRun = new(
        id: "WF0003",
        title: "Do not start work with Task.Run or Task.Factory.StartNew",
        messageFormat: "Start tracked work with QuiescenceScope.Run, or use a dedicated worker its owner joins",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Work started with Task.Run or Task.Factory.StartNew is neither tracked by an owner's quiescence scope nor ordered with respect to its shutdown.");

    public static readonly DiagnosticDescriptor BareAwaitableExpression = new(
        id: "WF0004",
        title: "Do not discard an unawaited awaitable expression",
        messageFormat: "Await this awaitable or start it as a tracked child with QuiescenceScope.Run",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A bare unawaited awaitable expression statement is reported by CS4014 only inside an async method; in a synchronous method the call is silently unobserved.");
}
