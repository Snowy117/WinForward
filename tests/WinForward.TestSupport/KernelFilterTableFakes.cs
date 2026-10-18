using WinForward.NdisApi;

namespace WinForward.TestSupport;

/// <summary>
/// An in-memory <see cref="INdisKernelFilterTable"/>: it installs whatever a replace hands it, reports
/// those rules' scripted counters on a read-back (rejecting a set it did not install), and records
/// every operation with the thread that issued it. The thread record is what proves the cache's
/// worker — never a pump thread — is the only driver caller.
/// </summary>
internal sealed class RecordingKernelFilterTable : INdisKernelFilterTable
{
    private readonly Lock _gate = new();
    private readonly List<string> _operations = [];
    private readonly List<long> _counters = [];
    private NdisTcpFlowRule[] _installed = [];

    public NdisKernelFilterStatus ResetStatus { get; set; } = NdisKernelFilterStatus.Success;

    public NdisKernelFilterStatus ReplaceStatus { get; set; } = NdisKernelFilterStatus.Success;

    public NdisKernelFilterStatus ReadStatus { get; set; } = NdisKernelFilterStatus.Success;

    public int NativeError { get; set; }

    public NdisTrafficCounters Traffic { get; set; }

    public int ResetCalls { get; private set; }

    public int ReplaceCalls { get; private set; }

    public int? LastOperationThreadId { get; private set; }

    /// <summary>The operations in call order: <c>reset</c>, <c>replace</c>, <c>read</c>.</summary>
    public IReadOnlyList<string> Operations
    {
        get { lock (_gate) return [.. _operations]; }
    }

    /// <summary>The rule set of the last successful replace (empty after a reset).</summary>
    public IReadOnlyList<NdisTcpFlowRule> InstalledRules
    {
        get { lock (_gate) return [.. _installed]; }
    }

    public NdisKernelFilterStatus Replace(ReadOnlySpan<NdisTcpFlowRule> rules, out int nativeError)
    {
        lock (_gate)
        {
            _operations.Add("replace");
            ReplaceCalls++;
            LastOperationThreadId = Environment.CurrentManagedThreadId;
            if (ReplaceStatus == NdisKernelFilterStatus.Success)
            {
                _installed = rules.ToArray();
                _counters.Clear();
                for (var index = 0; index < rules.Length; index++) _counters.Add(0);
            }
        }

        nativeError = NativeError;
        return ReplaceStatus;
    }

    public NdisKernelFilterStatus ReadRulePacketCounts(ReadOnlySpan<NdisTcpFlowRule> rules, Span<long> packetCounts, out int nativeError)
    {
        lock (_gate)
        {
            _operations.Add("read");
            LastOperationThreadId = Environment.CurrentManagedThreadId;
            if (ReadStatus != NdisKernelFilterStatus.Success)
            {
                nativeError = NativeError;
                return ReadStatus;
            }

            if (rules.Length != _installed.Length)
            {
                nativeError = 0;
                return NdisKernelFilterStatus.TableMismatch;
            }

            for (var index = 0; index < rules.Length; index++)
            {
                if (rules[index] != _installed[index])
                {
                    nativeError = 0;
                    return NdisKernelFilterStatus.TableMismatch;
                }
            }

            for (var index = 0; index < rules.Length; index++) packetCounts[index] = _counters[index];
        }

        nativeError = 0;
        return NdisKernelFilterStatus.Success;
    }

    public NdisKernelFilterStatus Reset(out int nativeError)
    {
        lock (_gate)
        {
            _operations.Add("reset");
            ResetCalls++;
            LastOperationThreadId = Environment.CurrentManagedThreadId;
            if (ResetStatus == NdisKernelFilterStatus.Success)
            {
                _installed = [];
                _counters.Clear();
            }
        }

        nativeError = NativeError;
        return ResetStatus;
    }

    public NdisTrafficCounters GetTrafficCounters() => Traffic;

    /// <summary>Advances the installed rules' per-rule counters, as traffic matching them would.</summary>
    public void AdvanceCounters(long packets)
    {
        lock (_gate)
        {
            for (var index = 0; index < _counters.Count; index++) _counters[index] += packets;
        }
    }

    /// <summary>Overwrites one installed rule's counter baseline (a driver reset shows as a decrease).</summary>
    public void SetCounter(int index, long value)
    {
        lock (_gate)
        {
            if (index >= 0 && index < _counters.Count) _counters[index] = value;
        }
    }
}
