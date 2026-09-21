using Microsoft.CodeAnalysis;

namespace WinForward.Analyzers;

/// <summary>
/// Decides whether a type is awaitable. Well-known task types are matched by identity; custom
/// awaitables are matched structurally on the public parameterless <c>GetAwaiter</c> pattern, which
/// is what <see langword="await"/> itself requires.
/// </summary>
internal static class AwaitableClassifier
{
    public static bool IsAwaitable(ITypeSymbol? type, WellKnownTypes types)
    {
        if (type is null || type.TypeKind == TypeKind.Error)
        {
            return false;
        }

        var definition = type.OriginalDefinition;
        return SymbolEqualityComparer.Default.Equals(definition, types.Task)
            || SymbolEqualityComparer.Default.Equals(definition, types.TaskOfT)
            || SymbolEqualityComparer.Default.Equals(definition, types.ValueTask)
            || SymbolEqualityComparer.Default.Equals(definition, types.ValueTaskOfT)
            || HasGetAwaiter(type);
    }

    private static bool HasGetAwaiter(ITypeSymbol? type)
    {
        for (var current = type; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
        {
            foreach (var member in current.GetMembers("GetAwaiter"))
            {
                if (member is IMethodSymbol { IsStatic: false, DeclaredAccessibility: Accessibility.Public, Parameters.Length: 0 } method
                    && HasAwaiterShape(method.ReturnType))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool HasAwaiterShape(ITypeSymbol awaiter)
    {
        var hasIsCompleted = false;
        var hasOnCompleted = false;
        var hasGetResult = false;
        foreach (var member in awaiter.GetMembers())
        {
            switch (member)
            {
                case IPropertySymbol { Name: "IsCompleted", IsStatic: false, Type.SpecialType: SpecialType.System_Boolean }:
                    hasIsCompleted = true;
                    break;
                case IMethodSymbol { Name: "OnCompleted", IsStatic: false, Parameters.Length: 1 } onCompleted
                    when IsParameterlessDelegate(onCompleted.Parameters[0].Type):
                    hasOnCompleted = true;
                    break;
                case IMethodSymbol { Name: "GetResult", IsStatic: false, Parameters.Length: 0 }:
                    hasGetResult = true;
                    break;
            }
        }

        return hasIsCompleted && hasOnCompleted && hasGetResult;
    }

    private static bool IsParameterlessDelegate(ITypeSymbol type) =>
        type is INamedTypeSymbol { TypeKind: TypeKind.Delegate, DelegateInvokeMethod.Parameters.Length: 0 };
}
