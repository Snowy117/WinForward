using WinForward.Benchmarks.Perf;
using Xunit;

namespace WinForward.Core.Tests;

/// <summary>
/// The R5 allocation gate: design §3 classifies the proxy path's allocation bytes as a gate, so the
/// BenchmarkDotNet <c>[MemoryDiagnoser]</c> 0 B column is enforced here instead of only reported. Each
/// composed leg — sequence tracking plus the leg's rewrite, as the micro rows define it — runs through
/// <see cref="TcpRedirectDataPathBenchmarks"/> itself, the same methods the rows time, so the gate
/// cannot drift from the number it pins. Both address families are covered at the full-size frame; the
/// IPv6 reverse leg's distinct rewrite is the one the 09-29 series flagged as 2.0× the IPv4 leg, and it
/// is the most likely of the four to start allocating a header buffer.
/// <para>
/// Measurement shape follows <c>HotPathAllocationGateTests</c>: warm the identical synchronous body
/// first, then bracket exactly one invocation with <see cref="GC.GetAllocatedBytesForCurrentThread"/>.
/// One invocation per window is deliberate — a per-packet allocation cannot hide behind an average, and
/// the exact zero is the contract <c>design.md</c> §3 gates.
/// </para>
/// </summary>
public sealed class TcpRedirectDataPathAllocationGateTests
{
    /// <summary>Warm-up iterations per leg: enough for JIT and tiering to promote the whole rewrite path before the measured window.</summary>
    private const int WarmupIterations = 64;

    /// <summary>The micro rows' full-size frame; the small row costs the same code paths, so the gate runs one size.</summary>
    private const int FrameSize = 1400;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForwardHostLegAllocatesNoManagedBytes(bool ipv6) => AssertLegAllocatesNoManagedBytes(CreateBenchmarks(ipv6).ForwardLegHost);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForwardForwardedLegAllocatesNoManagedBytes(bool ipv6) => AssertLegAllocatesNoManagedBytes(CreateBenchmarks(ipv6).ForwardLegForwarded);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReverseHostLegAllocatesNoManagedBytes(bool ipv6) => AssertLegAllocatesNoManagedBytes(CreateBenchmarks(ipv6).ReverseLegHost);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReverseForwardedLegAllocatesNoManagedBytes(bool ipv6) => AssertLegAllocatesNoManagedBytes(CreateBenchmarks(ipv6).ReverseLegForwarded);

    /// <summary>
    /// The rows' own setup: pristine frame, both association shapes, both families, and the
    /// rejection proof the benchmark class already performs — reused so the gate measures frames the
    /// rewriter accepts rather than a rejection path.
    /// </summary>
    private static TcpRedirectDataPathBenchmarks CreateBenchmarks(bool ipv6)
    {
        var benchmarks = new TcpRedirectDataPathBenchmarks { Ipv6 = ipv6, FrameSize = FrameSize };
        benchmarks.Setup();
        return benchmarks;
    }

    private static void AssertLegAllocatesNoManagedBytes(Func<bool> leg)
    {
        for (var warm = 0; warm < WarmupIterations; warm++)
        {
            if (!leg()) Assert.Fail("A warm-up call rejected the pristine frame; the measured window would price the rejection path.");
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        var rewritten = leg();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(rewritten, "The measured call rejected the pristine frame; the measured window priced the rejection path.");
        Assert.Equal(0, allocated);
    }
}
