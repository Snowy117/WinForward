using System.Buffers;
using System.Buffers.Binary;

namespace WinForward.NdisApi;

/// <summary>
/// The driver's static-filter-table surface (<see cref="INdisKernelFilterTable"/>). Every operation
/// is cold control state: it enters the control gate exactly once, encodes or verifies a whole table,
/// and maps a native FALSE to <see cref="NdisKernelFilterStatus.Failed"/> with the immediate
/// <c>Marshal.GetLastWin32Error()</c>.
/// </summary>
/// <remarks>
/// <para>
/// A missing export is <see cref="NdisKernelFilterStatus.Unavailable"/>, never an exception, so the
/// owner of the table (the kernel pass cache) can degrade without touching capture. A read-back that
/// disagrees with the last installed set is <see cref="NdisKernelFilterStatus.TableMismatch"/>: the
/// driver's static table is global, so that is a table-ownership conflict, not a native error.
/// </para>
/// <para>
/// The expected set is recorded on a successful replace and cleared by a reset or a failed replace.
/// Only the table's statistics block (and <c>LastReset</c>) may differ between what was installed and
/// what is read back; every other byte must match, which is what proves the table still belongs to
/// this driver.
/// </para>
/// </remarks>
public sealed partial class NdisApiDriver
{
    private const int LastResetOffset = 20;
    private const int PacketsInOffset = 24;
    private const int PacketsOutOffset = 40;
    private const int StatisticsEndOffset = 56;

    // The rule order this driver last installed, or null when the table is not known to be ours. Only
    // the filter-table operations below read or write it, and all of them run under the control gate.
    private NdisTcpFlowRule[]? _installedRules;

    /// <summary>Upper bound on concurrent control-gate holders, for the seam facts.</summary>
    internal int ControlGateMaxConcurrentCalls => _controlGate.MaxConcurrentCalls;

    public NdisKernelFilterStatus Replace(ReadOnlySpan<NdisTcpFlowRule> rules, out int nativeError)
    {
        ValidateRules(rules);
        nativeError = 0;
        if (_filterCalls is null) return NdisKernelFilterStatus.Unavailable;

        var byteCount = NdisStaticFilterAbi.EncodedSize(rules.Length);
        var buffer = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            if (!NdisStaticFilterAbi.TryWriteTable(buffer.AsSpan(0, byteCount), rules, out var written) || written != byteCount)
            {
                // Validation and the encoder share the same rule predicate, so this is unreachable
                // unless the two drift apart.
                throw new InvalidOperationException("The static filter table encoder rejected a rule set that passed validation.");
            }

            using var gateLease = _controlGate.Enter();
            try
            {
                if (!_filterCalls.SetPacketFilterTable(buffer.AsSpan(0, byteCount), out nativeError))
                {
                    _installedRules = null;
                    return NdisKernelFilterStatus.Failed;
                }
            }
            catch (Exception exception) when (exception is EntryPointNotFoundException or DllNotFoundException)
            {
                nativeError = 0;
                _installedRules = null;
                return NdisKernelFilterStatus.Unavailable;
            }

            _installedRules = rules.ToArray();
            return NdisKernelFilterStatus.Success;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public NdisKernelFilterStatus ReadRulePacketCounts(ReadOnlySpan<NdisTcpFlowRule> rules, Span<long> packetCounts, out int nativeError)
    {
        if (rules.IsEmpty) throw new ArgumentException("Reading the static filter table requires at least one rule.", nameof(rules));
        if (packetCounts.Length < rules.Length) throw new ArgumentException("The packet-count buffer must cover every rule.", nameof(packetCounts));
        nativeError = 0;
        if (_filterCalls is null) return NdisKernelFilterStatus.Unavailable;

        // The installed set and the table it names are one control-state read: another caller could
        // replace or reset the table between an ungated check and the read-back, so both run under
        // the same gate entry as the native calls.
        using var gateLease = _controlGate.Enter();
        try
        {
            // A read-back is only meaningful for the set this driver installed, in the order it
            // installed it; anything else means another client owns the table.
            var installed = _installedRules;
            if (installed is null || installed.Length != rules.Length) return NdisKernelFilterStatus.TableMismatch;
            for (var index = 0; index < rules.Length; index++)
            {
                if (installed[index] != rules[index]) return NdisKernelFilterStatus.TableMismatch;
            }

            if (!_filterCalls.GetPacketFilterTableSize(out var tableSize, out nativeError)) return NdisKernelFilterStatus.Failed;
            var expectedRecordCount = checked((uint)(rules.Length * 2));
            if (tableSize != expectedRecordCount) return NdisKernelFilterStatus.TableMismatch;

            var byteCount = NdisStaticFilterAbi.TableHeaderSize + checked((int)tableSize * NdisStaticFilterAbi.StaticFilterSize);
            return ReadInstalledTable(rules, packetCounts, installed, byteCount, expectedRecordCount, out nativeError);
        }
        catch (Exception exception) when (exception is EntryPointNotFoundException or DllNotFoundException)
        {
            nativeError = 0;
            return NdisKernelFilterStatus.Unavailable;
        }
    }

    public NdisKernelFilterStatus Reset(out int nativeError)
    {
        nativeError = 0;
        if (_filterCalls is null) return NdisKernelFilterStatus.Unavailable;

        using var gateLease = _controlGate.Enter();
        try
        {
            if (!_filterCalls.ResetPacketFilterTable(out nativeError))
            {
                _installedRules = null;
                return NdisKernelFilterStatus.Failed;
            }
        }
        catch (Exception exception) when (exception is EntryPointNotFoundException or DllNotFoundException)
        {
            nativeError = 0;
            _installedRules = null;
            return NdisKernelFilterStatus.Unavailable;
        }

        _installedRules = null;
        return NdisKernelFilterStatus.Success;
    }

    public NdisTrafficCounters GetTrafficCounters() => new(
        Interlocked.Read(ref _batchReads),
        Interlocked.Read(ref _packetsRead),
        Interlocked.Read(ref _sendCalls),
        Interlocked.Read(ref _packetsReinjected));

    private NdisKernelFilterStatus ReadInstalledTable(
        ReadOnlySpan<NdisTcpFlowRule> rules,
        Span<long> packetCounts,
        NdisTcpFlowRule[] installed,
        int byteCount,
        uint expectedRecordCount,
        out int nativeError)
    {
        nativeError = 0;
        var table = ArrayPool<byte>.Shared.Rent(byteCount);
        var expectedBytes = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            var readBack = table.AsSpan(0, byteCount);
            readBack.Clear();
            // The user-mode SDK sizes the output copy from this field before the IOCTL, so the caller
            // must write the record count into the read-back header first.
            BinaryPrimitives.WriteUInt32LittleEndian(readBack, expectedRecordCount);
            if (!_filterCalls!.GetPacketFilterTable(readBack, out nativeError)) return NdisKernelFilterStatus.Failed;

            var expected = expectedBytes.AsSpan(0, byteCount);
            if (!NdisStaticFilterAbi.TryWriteTable(expected, installed, out _)) return NdisKernelFilterStatus.TableMismatch;

            for (var index = 0; index < rules.Length; index++)
            {
                long packets = 0;
                for (var oriented = 0; oriented < 2; oriented++)
                {
                    var offset = NdisStaticFilterAbi.TableHeaderSize + (((index * 2) + oriented) * NdisStaticFilterAbi.StaticFilterSize);
                    var actualEntry = readBack.Slice(offset, NdisStaticFilterAbi.StaticFilterSize);
                    var expectedEntry = expected.Slice(offset, NdisStaticFilterAbi.StaticFilterSize);
                    if (!EntryMatches(actualEntry, expectedEntry)) return NdisKernelFilterStatus.TableMismatch;
                    packets += (long)BinaryPrimitives.ReadUInt64LittleEndian(actualEntry[PacketsInOffset..]);
                    packets += (long)BinaryPrimitives.ReadUInt64LittleEndian(actualEntry[PacketsOutOffset..]);
                }

                packetCounts[index] = packets;
            }

            return NdisKernelFilterStatus.Success;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(expectedBytes);
            ArrayPool<byte>.Shared.Return(table);
        }
    }

    /// <summary>
    /// Compares one entry against what this driver installed. The driver owns <c>LastReset</c> and the
    /// four statistics fields between it and the data-link selector, so those are excluded; every
    /// other byte — adapter, direction flags, action, the selector bits, addresses, masks, ports, and
    /// protocol — must be identical.
    /// </summary>
    private static bool EntryMatches(ReadOnlySpan<byte> actual, ReadOnlySpan<byte> expected) =>
        actual[..LastResetOffset].SequenceEqual(expected[..LastResetOffset])
        && actual[StatisticsEndOffset..].SequenceEqual(expected[StatisticsEndOffset..]);

    private static void ValidateRules(ReadOnlySpan<NdisTcpFlowRule> rules)
    {
        if (rules.IsEmpty)
        {
            throw new ArgumentException("The static filter table cannot be replaced with zero rules; clear it with Reset instead.", nameof(rules));
        }

        if (rules.Length > NdisStaticFilterAbi.MaximumRules)
        {
            throw new ArgumentOutOfRangeException(nameof(rules), rules.Length, "The rule set does not fit the addressable static filter table.");
        }

        for (var index = 0; index < rules.Length; index++)
        {
            if (!rules[index].IsEncodable)
            {
                throw new ArgumentException("Every mirrored flow must be an unscoped, non-zero-port TCP tuple with a shared address family.", nameof(rules));
            }
        }
    }
}
