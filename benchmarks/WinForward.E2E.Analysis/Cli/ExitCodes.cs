namespace WinForward.E2E.Analysis.Cli;

/// <summary>
/// The exit codes the analysis shares with the reference implementation it is diffed against: the
/// oracle reads them, so they are part of the contract rather than a convenience.
/// </summary>
internal static class ExitCodes
{
    internal const int Success = 0;

    /// <summary>
    /// Anything the caller can fix by changing the invocation or the tree it points at: an argument
    /// the walk refuses, a <c>--raw</c> directory that does not exist, a tree with no rows, an
    /// unreadable ledger. The reference returns <c>2</c> for all of them.
    /// </summary>
    internal const int InputError = 2;
}
