namespace WinForward.E2E.Contracts.Json;

/// <summary>
/// What a <see cref="JsonlSink"/> does when one of its records cannot be written.
/// </summary>
public enum JsonlPolicy
{
    /// <summary>
    /// The caller owns the failure: the write throws, and a close reports the failure through
    /// <see cref="JsonlSink.WriteErrors"/> instead of throwing. Used by the client, where a lost
    /// record is the caller's evidence and has to end the arm rather than pass silently.
    /// </summary>
    Propagate = 0,

    /// <summary>
    /// Nothing throws and every failure is counted. Used by the target's ledger, which observes the
    /// run: a full or dead disk must not take the target down before its summaries are written.
    /// </summary>
    SwallowAndCount = 1,
}
