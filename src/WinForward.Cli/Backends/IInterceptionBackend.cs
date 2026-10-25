namespace WinForward.Cli.Backends;

/// <summary>
/// One interception backend, selected by <c>WinForward.BackendDriver.Provider</c> at the CLI
/// composition boundary. A backend owns everything below configuration, logging, platform checks
/// and exit-code mapping: <see cref="RunAsync"/> opens, composes and tears down its own driver and
/// capture runtime, then reports the exit code it concluded.
/// </summary>
internal interface IInterceptionBackend : IAsyncDisposable
{
    /// <summary>
    /// The provider this backend implements, named exactly as the configuration and the resolved-run
    /// report name it. The report stays the single place the selected provider is logged; a backend
    /// must not emit it again.
    /// </summary>
    string ProviderName { get; }

    /// <summary>
    /// Runs interception until <paramref name="cancellationToken"/> requests shutdown or the run
    /// ends on its own, and returns the exit code: 0 clean, 1 configuration or capture-scope
    /// resolution, 3 no capture adapters or a fatal runtime fault. Faults whose CLI diagnostics are
    /// specific — a missing NDISAPI library, a missing export, an ABI mismatch, a native driver
    /// error — escape to the caller unchanged.
    /// </summary>
    /// <remarks>
    /// The implementation owns its entire lifetime inside this call. The caller's
    /// <see cref="IAsyncDisposable.DisposeAsync"/> never releases a run that is still going: it is a
    /// join on what the run already tore down, so a backend whose composition fits one call may
    /// implement it as a no-op.
    /// </remarks>
    ValueTask<int> RunAsync(CancellationToken cancellationToken);
}
