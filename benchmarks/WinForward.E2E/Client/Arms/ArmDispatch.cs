namespace WinForward.E2E.Client.Arms;

internal static class ArmDispatch
{
    internal static Task<ArmOutcome> RunAsync(ArmContext context) =>
        ArmKind.Find(context.Spec.Kind) is { } kind
            ? kind.Run(context)
            : throw new InvalidOperationException($"unknown arm kind '{context.Spec.Kind}'");
}
