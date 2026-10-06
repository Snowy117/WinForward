namespace WinForward.E2E.Target;

/// <summary>
/// A target-side diagnostic from a path that must survive its own failure. The target runs
/// unattended with stderr on a pipe, so a diagnostic that throws at a closed pipe would end the run
/// it is reporting on.
/// </summary>
internal static class TargetLog
{
    internal static async ValueTask ReportAsync(string message)
    {
        try
        {
            await Console.Error.WriteLineAsync(message).ConfigureAwait(false);
        }
        catch (IOException)
        {
            /* stderr may be a closed pipe; a diagnostic must never end the target */
        }
        catch (ObjectDisposedException)
        {
            /* stderr may be a closed pipe; a diagnostic must never end the target */
        }
    }
}
