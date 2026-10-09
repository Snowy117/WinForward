using System.Globalization;
using System.Runtime.CompilerServices;
using WinForward.Protocols;
using WinForward.Runtime;
using Xunit;
using Xunit.Abstractions;

namespace WinForward.Core.Tests;

/// <summary>
/// The struct-size contract of the per-packet value types (hot-path.md, "Packets are structs with
/// pinned shapes"): the sizes are
/// asserted exactly, so a shape change fails here instead of silently growing the structs a packet
/// copies.
/// </summary>
public sealed class FlowKeyShapeTests(ITestOutputHelper output)
{
    [Fact]
    public void StructSizesForDiagnostics()
    {
        var sizes = new (string Name, int Size, int Expected)[]
        {
            ("FlowKey", Unsafe.SizeOf<FlowKey>(), 64),
            ("FlowContext", Unsafe.SizeOf<FlowContext>(), 80),
            ("CapturedFlowPacket", Unsafe.SizeOf<CapturedFlowPacket>(), 152),
            ("FlowStateView", Unsafe.SizeOf<FlowStateView>(), 96),
            ("PacketView", Unsafe.SizeOf<PacketView>(), 96),
            ("Endpoint", Unsafe.SizeOf<Endpoint>(), 48),
        };

        foreach (var (name, size, expected) in sizes)
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Unsafe.SizeOf<{name}>() = {size} (expected {expected})"));
            Assert.Equal(expected, size);
        }
    }
}
