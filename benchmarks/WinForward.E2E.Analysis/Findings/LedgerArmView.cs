namespace WinForward.E2E.Analysis.Findings;

/// <summary>One arm's window as the ledger sees it: what the ledger recorded, and what the client claims.</summary>
internal sealed class LedgerArmView
{
    /// <summary>The pass and the run whose window this is.</summary>
    internal required string Pass { get; init; }

    /// <summary>The run id the window belongs to.</summary>
    internal required string Row { get; init; }

    /// <summary>The row that owns the run; a lane's owner is its row.</summary>
    internal required string Owner { get; init; }

    /// <summary>The arm.</summary>
    internal required string Arm { get; init; }

    /// <summary>Other runs whose windows overlap this one.</summary>
    internal required IReadOnlyList<string> OverlappingRuns { get; init; }

    /// <summary>Whether the overlap makes the window's records unattributable.</summary>
    internal required bool Unattributable { get; init; }

    /// <summary>How many TCP connections the ledger saw in the window.</summary>
    internal required int TcpConnections { get; init; }

    /// <summary>How many datagrams the window's endpoint census accounts for.</summary>
    internal required double UdpDatagrams { get; init; }

    /// <summary>The endpoints the window's census names.</summary>
    internal required IReadOnlyList<string> UdpEndpoints { get; init; }

    /// <summary>The endpoint census table's overflow count.</summary>
    internal required int UdpOverflow { get; init; }

    /// <summary>What the client says it sent, or null when its own counters do not cover the arm.</summary>
    internal double? ClientConnections { get; set; }

    /// <summary>Where the client's connection count comes from.</summary>
    internal string? ClientConnectionsSource { get; set; }

    /// <summary>What the client says it sent as datagrams, or null.</summary>
    internal double? ClientDatagrams { get; set; }

    /// <summary>Where the client's datagram count comes from.</summary>
    internal string? ClientDatagramsSource { get; set; }

    /// <summary>How long the arm ran, which sets the datagram tolerance's floor.</summary>
    internal double DurationSeconds { get; set; }
}
