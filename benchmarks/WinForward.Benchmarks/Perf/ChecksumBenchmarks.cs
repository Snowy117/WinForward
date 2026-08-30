using System.Buffers.Binary;
using System.Runtime.Intrinsics;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using WinForward.Protocols;

namespace WinForward.Benchmarks.Perf;

/// <summary>
/// Internet-checksum micro for the P2b decision: the production scalar
/// <see cref="PacketChecksums.InternetChecksum"/> against a Vector256 candidate (pairwise
/// u32-lane accumulate + byte-swap shuffle + scalar tail, bit-identical fold semantics).
/// This feeds the rebuild-path shape (UDP response rebuild, IPv4 header checksum): the
/// vectorized version lands in production only if it clears a &gt;20 % win here.
/// </summary>
[MemoryDiagnoser]
public class ChecksumBenchmarks
{
    [Params(64, 512, 1514)]
    public int FrameBytes { get; set; }

    private byte[] _data = null!;

    [GlobalSetup]
    public void Setup()
    {
        _data = new byte[FrameBytes];
        for (var index = 0; index < _data.Length; index++) _data[index] = unchecked((byte)(index * 31 + 7));
    }

    [Benchmark(Baseline = true)]
    public ushort Scalar() => PacketChecksums.InternetChecksum(_data);

    [Benchmark]
    public ushort VectorCandidate() => VectorizedCandidate.InternetChecksum(_data);

    [GlobalCleanup]
    public void VerifyEquivalence()
    {
        // Deterministic equivalence pin on the actual benchmark input (property coverage lives in
        // the test suite); a mismatch would invalidate every timing above.
        if (Scalar() != VectorCandidate()) throw new InvalidOperationException("vector candidate diverges from scalar checksum");
    }
}

internal static class VectorizedCandidate
{
    private static readonly Vector256<byte> SwapAdjacentBytes = Vector256.Create(
        (byte)1, 0, 3, 2, 5, 4, 7, 6, 9, 8, 11, 10, 13, 12, 15, 14,
        17, 16, 19, 18, 21, 20, 23, 22, 25, 24, 27, 26, 29, 28, 31, 30);

    public static ushort InternetChecksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        var index = 0;
        if (Vector256.IsHardwareAccelerated && data.Length >= 64)
        {
            var accumulator = Vector256<uint>.Zero;
            ref byte start = ref MemoryMarshal.GetReference(data);
            for (; index + Vector256<byte>.Count <= data.Length; index += Vector256<byte>.Count)
            {
                var block = Vector256.LoadUnsafe(ref start, (nuint)index);
                var swapped = Vector256.Shuffle(block, SwapAdjacentBytes);
                var (lo, hi) = Vector256.Widen(swapped.AsUInt16());
                accumulator += lo + hi;
            }
            // Each u32 lane held at most (data.Length / 32) + 1 words of <= 0xFFFF, so this
            // horizontal add cannot wrap for any span bounded by the 64 KiB IP maximum.
            sum = Vector256.Sum(accumulator);
        }
        for (; index + 1 < data.Length; index += 2)
        {
            sum += BinaryPrimitives.ReadUInt16BigEndian(data.Slice(index, 2));
        }
        if (index < data.Length)
        {
            sum += (uint)data[index] << 8;
        }
        while (sum >> 16 != 0) sum = (sum & 0xffff) + (sum >> 16);
        return (ushort)~sum;
    }
}
