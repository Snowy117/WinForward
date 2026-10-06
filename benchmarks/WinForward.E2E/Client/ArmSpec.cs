using System.Runtime.InteropServices;
using WinForward.E2E.Wire;

namespace WinForward.E2E.Client;

[StructLayout(LayoutKind.Auto)]
internal readonly struct ModeWeight
{
    internal ModeWeight(TcpMode mode, int weight)
    {
        Mode = mode;
        Weight = weight;
    }

    internal TcpMode Mode { get; }

    internal int Weight { get; }
}

internal sealed class ArmSpec
{
    internal const string DefaultModeMix = "clean=25,resetAfterN=25,partialFin=25,halfClose=25";

    internal string Name { get; set; } = string.Empty;

    internal string Kind { get; set; } = string.Empty;

    internal double Seconds { get; set; } = 60;

    internal int RatePerSecond { get; set; }

    internal int PayloadBytes { get; set; }

    internal int IntervalMs { get; set; }

    internal int IdleSeconds { get; set; }

    internal string Protocol { get; set; } = "tcp";

    internal int ConnectionsPerSecond { get; set; }

    internal string ModeMix { get; set; } = string.Empty;

    internal int Streams { get; set; }

    internal long TargetBytesPerSecond { get; set; }

    internal int TcpPercent { get; set; }

    internal int CnameEvery { get; set; }

    internal int Desktops { get; set; }

    internal int Window { get; set; }

    /// <summary>
    /// The loss threshold the UDP arms publish and classify against. Distinct from
    /// <see cref="Window"/>, which caps how many requests a latency lane may keep in flight.
    /// </summary>
    internal int LossWindowMs { get; set; }

    /// <summary>
    /// Overrides the run's DNS port for this arm. Several products special-case destination port
    /// 53, so measuring a comparison of DNS through the proxy needs a port none of them knows.
    /// Zero means "use the port the run was started with".
    /// </summary>
    internal int DnsPort { get; set; }

    internal int Lanes { get; set; }

    internal int ExpectedBytes { get; set; }
}
