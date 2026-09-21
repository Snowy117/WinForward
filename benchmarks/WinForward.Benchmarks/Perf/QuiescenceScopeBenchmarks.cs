using BenchmarkDotNet.Attributes;
using WinForward.Runtime;

namespace WinForward.Benchmarks.Perf;

/// <summary>
/// Gate-choice comparison for <see cref="QuiescenceScope"/> (task 09-20-quiescence-scope §7).
/// The production type uses the winning gate; the two state helpers below keep both variants
/// measurable side by side so a future session can re-run the comparison.
/// </summary>
[MemoryDiagnoser]
public class QuiescenceScopeBenchmarks
{
    private LockGateState _lockGate = null!;
    private PackedGateState _packedGate = null!;
    private QuiescenceScope _scope = null!;

    [GlobalSetup]
    public void Setup()
    {
        _lockGate = new LockGateState();
        _packedGate = new PackedGateState();
        _scope = new QuiescenceScope();
    }

    [GlobalCleanup]
    public void SealGates()
    {
        _lockGate.Seal();
        _packedGate.Seal();
    }

    [Benchmark(Baseline = true)]
    public void LockGateEnterExit()
    {
        if (!_lockGate.TryEnter())
        {
            throw new InvalidOperationException("lock gate unexpectedly sealed.");
        }

        _lockGate.Exit();
    }

    [Benchmark]
    public void PackedGateEnterExit()
    {
        if (!_packedGate.TryEnter())
        {
            throw new InvalidOperationException("packed gate unexpectedly sealed.");
        }

        _packedGate.Exit();
    }

    [Benchmark]
    public void ProductionEnterExit()
    {
        if (!_scope.TryEnter(out var lease))
        {
            throw new InvalidOperationException("scope unexpectedly sealed.");
        }

        lease.Dispose();
    }

    private sealed class LockGateState
    {
        private readonly Lock _gate = new();
        private int _pending;
        private bool _sealed;

        public bool TryEnter()
        {
            lock (_gate)
            {
                if (_sealed)
                {
                    return false;
                }

                _pending++;
                return true;
            }
        }

        public void Exit()
        {
            lock (_gate)
            {
                if (--_pending < 0)
                {
                    throw new InvalidOperationException("Gate underflow.");
                }
            }
        }

        public void Seal()
        {
            lock (_gate)
            {
                _sealed = true;
            }
        }
    }

    private sealed class PackedGateState
    {
        private int _state;

        public bool TryEnter()
        {
            while (true)
            {
                var current = Volatile.Read(ref _state);
                if ((current & 1) != 0)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _state, current + 2, current) == current)
                {
                    return true;
                }
            }
        }

        public void Exit()
        {
            if (Interlocked.Add(ref _state, -2) < 0)
            {
                throw new InvalidOperationException("Gate underflow.");
            }
        }

        public void Seal() => Interlocked.Or(ref _state, 1);
    }
}
