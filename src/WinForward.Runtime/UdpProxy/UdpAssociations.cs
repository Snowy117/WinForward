using WinForward.Core;

namespace WinForward.Runtime.UdpProxy;

public readonly record struct RelayAlias(FlowKey LocalRelayToRemoteRelay);

public sealed class UdpAssociation
{
    internal UdpAssociation(FlowKey originalKey, RelayAlias relayAlias, long generation, DateTimeOffset now)
    {
        OriginalKey = originalKey;
        RelayAlias = relayAlias;
        Generation = generation;
        LastActivityUtc = now;
    }

    public FlowKey OriginalKey { get; }
    public RelayAlias RelayAlias { get; }
    public long Generation { get; }
    public DateTimeOffset LastActivityUtc { get; private set; }

    public void Touch(DateTimeOffset now) => LastActivityUtc = now;
}

public sealed class UdpAssociationTable
{
    private readonly Dictionary<FlowKey, UdpAssociation> _byOriginal = [];
    private readonly Dictionary<RelayAlias, UdpAssociation> _byRelay = [];
    private readonly Lock _gate = new();

    // Single flight for the sweep, never taken while _gate is held: the sweep collects candidates
    // into a reused scratch and then removes them one short hold at a time.
    private readonly Lock _sweepGate = new();
    private readonly List<UdpAssociation> _expiredScratch = [];
    private readonly int _capacity;
    private long _nextGeneration;

    public UdpAssociationTable(int capacity = 16_384, int initialCapacity = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegative(initialCapacity);
        _capacity = capacity;
        if (initialCapacity > 0)
        {
            _byOriginal = new Dictionary<FlowKey, UdpAssociation>(initialCapacity);
            _byRelay = new Dictionary<RelayAlias, UdpAssociation>(initialCapacity);
        }
    }

    public UdpAssociation Claim(FlowKey originalKey, RelayAlias relayAlias, DateTimeOffset now)
    {
        if (!TryClaim(originalKey, relayAlias, now, out var association) || association is null) throw new InvalidOperationException("UDP association table capacity has been reached.");
        return association;
    }

    public bool TryClaim(FlowKey originalKey, RelayAlias relayAlias, DateTimeOffset now, out UdpAssociation? association)
        => TryClaim(originalKey, relayAlias, now, out association, out _);

    public bool TryClaim(FlowKey originalKey, RelayAlias relayAlias, DateTimeOffset now, out UdpAssociation? association, out bool created)
    {
        lock (_gate)
        {
            if (_byOriginal.TryGetValue(originalKey, out var existing))
            {
                existing.Touch(now);
                association = existing;
                created = false;
                return true;
            }

            if (_byRelay.TryGetValue(relayAlias, out var relayExisting))
            {
                if (relayExisting.OriginalKey == originalKey)
                {
                    relayExisting.Touch(now);
                    association = relayExisting;
                    created = false;
                    return true;
                }

                // A relay tuple already belongs to another logical flow. Sharing it
                // would make reverse datagrams impossible to route deterministically.
                association = null;
                created = false;
                return false;
            }

            if (_byOriginal.Count >= _capacity)
            {
                association = null;
                created = false;
                return false;
            }

            var newAssociation = new UdpAssociation(originalKey, relayAlias, ++_nextGeneration, now);
            _byOriginal.Add(originalKey, newAssociation);
            _byRelay.Add(relayAlias, newAssociation);
            association = newAssociation;
            created = true;
            return true;
        }
    }

    public bool TryFindOriginal(FlowKey originalKey, DateTimeOffset now, out UdpAssociation? association) => TryFind(_byOriginal, originalKey, now, out association);

    public bool TryFindRelay(RelayAlias relayAlias, DateTimeOffset now, out UdpAssociation? association) => TryFind(_byRelay, relayAlias, now, out association);

    public bool TryTouch(UdpAssociation expected, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(expected);
        lock (_gate)
        {
            if (!_byOriginal.TryGetValue(expected.OriginalKey, out var association) || !ReferenceEquals(association, expected)) return false;
            association.Touch(now);
            return true;
        }
    }

    public bool TryRemove(UdpAssociation expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        lock (_gate)
        {
            if (!_byOriginal.TryGetValue(expected.OriginalKey, out var association) || !ReferenceEquals(association, expected)) return false;
            _byOriginal.Remove(expected.OriginalKey);
            _byRelay.Remove(expected.RelayAlias);
            return true;
        }
    }

    /// <summary>
    /// Removes associations idle past <paramref name="idleTimeout"/>. The table has <em>no production
    /// caller</em> — tests only — but it carries the same contract as the other sweep sites so "every sweep
    /// site allocates nothing" holds repo-wide: one scan hold into a reused scratch, then one short hold per
    /// removal that re-checks the association's presence and idleness.
    /// </summary>
    public int RemoveExpired(DateTimeOffset now, TimeSpan idleTimeout)
    {
        lock (_sweepGate)
        {
            _expiredScratch.Clear();
            lock (_gate)
            {
                foreach (var association in _byOriginal.Values)
                {
                    if (now - association.LastActivityUtc >= idleTimeout) _expiredScratch.Add(association);
                }
            }

            var removed = 0;
            foreach (var association in _expiredScratch)
            {
                lock (_gate)
                {
                    // Re-check under the gate: a lookup may have touched the association, or a teardown may
                    // have removed it, since the scan released it.
                    if (!_byOriginal.TryGetValue(association.OriginalKey, out var current) ||
                        !ReferenceEquals(current, association) ||
                        now - association.LastActivityUtc < idleTimeout)
                    {
                        continue;
                    }

                    _byOriginal.Remove(association.OriginalKey);
                    _byRelay.Remove(association.RelayAlias);
                    removed++;
                }
            }

            return removed;
        }
    }

    private bool TryFind<TKey>(Dictionary<TKey, UdpAssociation> table, TKey key, DateTimeOffset now, out UdpAssociation? association) where TKey : notnull
    {
        lock (_gate)
        {
            if (table.TryGetValue(key, out association))
            {
                association.Touch(now);
                return true;
            }
            association = null;
            return false;
        }
    }
}
