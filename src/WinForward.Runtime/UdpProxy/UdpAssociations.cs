using WinForward.Core;

namespace WinForward.Runtime.UdpProxy;

public readonly record struct RelayAlias(FlowKey LocalRelayToRemoteRelay) : IEquatable<RelayAlias>;

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
    private readonly int _capacity;
    private long _nextGeneration;

    public UdpAssociationTable(int capacity = 16_384, int initialCapacity = 0)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (initialCapacity < 0) throw new ArgumentOutOfRangeException(nameof(initialCapacity));
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
    /// Removes a specific original flow from both indexes. Used when a session is torn down so a
    /// relay alias is released for reuse and no half-claimed association lingers.
    /// </summary>
    public bool TryRemoveOriginal(FlowKey originalKey)
    {
        lock (_gate)
        {
            if (!_byOriginal.TryGetValue(originalKey, out var association)) return false;
            _byOriginal.Remove(originalKey);
            _byRelay.Remove(association.RelayAlias);
            return true;
        }
    }

    public int RemoveExpired(DateTimeOffset now, TimeSpan idleTimeout)
    {
        lock (_gate)
        {
            var expired = _byOriginal.Values.Where(value => now - value.LastActivityUtc >= idleTimeout).Distinct().ToArray();
            foreach (var association in expired)
            {
                _byOriginal.Remove(association.OriginalKey);
                _byRelay.Remove(association.RelayAlias);
            }
            return expired.Length;
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
