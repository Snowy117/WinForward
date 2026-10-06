using System.Buffers.Binary;
using BenchmarkDotNet.Attributes;
using WinForward.Protocols;

namespace WinForward.Benchmarks.Perf;

/// <summary>
/// Internet-checksum micro for the P2b decision: the production entry point against a scalar form on
/// the same 4 096-word fold cadence, so the ratio measures vectorization rather than fold frequency.
/// Which tier the production side exercises depends on the build — 512-bit under the JIT, 128-bit
/// <c>Vector&lt;T&gt;</c> in a Native AOT build at <c>IlcInstructionSet=base</c>.
/// <c>FrameBytes=20</c> is the IPv4-header case: below the 256/512-bit widths, so it is entirely
/// tail there, and one 128-bit <c>Vector&lt;T&gt;</c> iteration plus a 4-byte tail at
/// <c>IlcInstructionSet=base</c>.
/// </summary>
[MemoryDiagnoser]
public class ChecksumBenchmarks
{
    [Params(20, 64, 512, 1514)]
    public int FrameBytes { get; set; }

    private byte[] _data = null!;

    [GlobalSetup]
    public void Setup()
    {
        _data = new byte[FrameBytes];
        for (var index = 0; index < _data.Length; index++) _data[index] = unchecked((byte)((index * 31) + 7));
    }

    [Benchmark(Baseline = true)]
    public ushort ScalarFallback() => ScalarReference.InternetChecksum(_data);

    [Benchmark]
    public ushort Production() => PacketChecksums.InternetChecksum(_data);

    [GlobalCleanup]
    public void VerifyEquivalence()
    {
        // Deterministic equivalence pin on the actual benchmark input (property coverage lives in
        // the test suite); a mismatch would invalidate every timing above.
        if (ScalarFallback() != Production()) throw new InvalidOperationException("production checksum diverges from the scalar fallback");
    }
}

/// <summary>
/// The scalar reference the vector path must beat: fold-while-adding on the same 4 096-word cadence
/// the production tail uses, so the comparison isolates vectorization rather than fold frequency.
/// </summary>
internal static class ScalarReference
{
    private const int FoldWordInterval = 4_096;

    public static ushort InternetChecksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        var index = 0;
        var wordsSinceFold = 0;
        for (; index + 1 < data.Length; index += 2)
        {
            sum += BinaryPrimitives.ReadUInt16BigEndian(data.Slice(index, 2));
            if (++wordsSinceFold != FoldWordInterval) continue;
            while (sum >> 16 != 0) sum = (sum & 0xffff) + (sum >> 16);
            wordsSinceFold = 0;
        }

        if (index < data.Length) sum += (uint)data[index] << 8;
        while (sum >> 16 != 0) sum = (sum & 0xffff) + (sum >> 16);
        return (ushort)~sum;
    }
}
