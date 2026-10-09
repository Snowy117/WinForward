using System.Text.Json;
using WinForward.E2E.Contracts;

namespace WinForward.E2E.Client;

internal sealed class LatencySet
{
    internal LogHistogram TcpConnect { get; } = new();

    internal LogHistogram TcpRtt { get; } = new();

    internal LogHistogram UdpRtt { get; } = new();

    internal LogHistogram DnsRtt { get; } = new();

    internal void WriteTo(Utf8JsonWriter writer)
    {
        writer.WriteStartObject(ArmKeys.Common.Record.Latency);
        Write(writer, ArmKeys.Common.LatencyRecord.TcpConnect, TcpConnect);
        Write(writer, ArmKeys.Common.LatencyRecord.TcpRtt, TcpRtt);
        Write(writer, ArmKeys.Common.LatencyRecord.UdpRtt, UdpRtt);
        Write(writer, ArmKeys.Common.LatencyRecord.DnsRtt, DnsRtt);
        writer.WriteEndObject();
    }

    private static void Write(Utf8JsonWriter writer, string name, LogHistogram histogram)
    {
        if (histogram.Count > 0)
        {
            histogram.Snapshot().WriteTo(writer, name);
        }
    }
}
