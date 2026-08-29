using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;
using WinForward.Core;

namespace WinForward.Windows;

/// <summary>
/// A hardware-independent projection of one network interface's unicast addresses, mirroring the
/// <see cref="IPAdapterInfo"/> projection in <see cref="AdapterIdentity.cs"/>. The mask is captured
/// only for IPv4 addresses; IPv6 subnet preference uses /64 prefix equality instead.
/// </summary>
public readonly record struct IPAdapterUnicastInfo(string Id, IReadOnlyList<IPAdapterUnicastAddress> Addresses);

public readonly record struct IPAdapterUnicastAddress(IPAddress Address, IPAddress? Ipv4Mask);

public interface IAdapterLocalAddressProvider
{
    /// <summary>
    /// Selects the local unicast address of <paramref name="adapterId"/> that a transparent-redirect
    /// destination should be rewritten to for a flow of <paramref name="family"/> arriving from
    /// <paramref name="clientAddress"/>. Returns null when the adapter cannot be correlated or owns
    /// no usable address of the family (no loopback, no IPv6 link-local), so the caller fails closed.
    /// </summary>
    IPAddress? SelectLocalAddress(string adapterId, AddressFamilyKind family, IPAddress clientAddress);
}

/// <summary>
/// Resolves adapter-local addresses from a cached <see cref="NetworkInterface"/> snapshot.
/// Correlation with the NDISAPI-plane adapter id follows the GUID-primary rule of
/// <see cref="WindowsAdapterInventory"/> (brace/case-insensitive, raw-string equality as
/// fallback). Among the adapter's addresses of the requested family, an address sharing the
/// client's subnet is preferred so the rewritten packet looks like ordinary same-subnet traffic
/// to the local stack. The snapshot is rebuilt lazily when a
/// <see cref="NetworkChange.NetworkAddressChanged"/> event invalidates it or when the safety TTL
/// lapses (change events can be coalesced or missed), so forwarded-SYN bursts enumerate the
/// interfaces at most once per window instead of once per SYN on the capture pump thread.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsAdapterLocalAddressProvider : IAdapterLocalAddressProvider
{
    /// <summary>
    /// Bounds snapshot staleness when no change event arrives. Deliberately generous: the cache
    /// exists to remove per-SYN enumeration, and a forwarded-flow address that goes stale between
    /// events is corrected by the next rebuild within this window.
    /// </summary>
    private static readonly TimeSpan SnapshotSafetyTimeToLive = TimeSpan.FromSeconds(30);

    private readonly Func<IReadOnlyList<IPAdapterUnicastInfo>> _adapters;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _rebuildGate = new();
    private AddressSnapshot? _snapshot;

    public WindowsAdapterLocalAddressProvider(Func<IReadOnlyList<IPAdapterUnicastInfo>>? adapters = null)
        : this(adapters, TimeProvider.System, subscribeAddressChanged: null)
    {
    }

    /// <summary>
    /// Full-seam construction: injectable enumeration, clock (TTL), and change-event
    /// subscription so tests drive invalidation deterministically instead of hooking the real
    /// system event.
    /// </summary>
    internal WindowsAdapterLocalAddressProvider(
        Func<IReadOnlyList<IPAdapterUnicastInfo>>? adapters,
        TimeProvider timeProvider,
        Action<Action>? subscribeAddressChanged)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _adapters = adapters ?? GetWindowsAdapterAddresses;
        _timeProvider = timeProvider;
        // Process-lifetime subscription by design: the provider is a composition-root singleton
        // that outlives every capture pump, so no unsubscribe is kept.
        (subscribeAddressChanged ?? SubscribeNetworkAddressChanged)(InvalidateSnapshot);
    }

    private static void SubscribeNetworkAddressChanged(Action callback) =>
        NetworkChange.NetworkAddressChanged += (_, _) => callback();

    private static IReadOnlyList<IPAdapterUnicastInfo> GetWindowsAdapterAddresses()
    {
        var result = new List<IPAdapterUnicastInfo>();
        foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
        {
            var addresses = new List<IPAdapterUnicastAddress>();
            foreach (var unicast in network.GetIPProperties().UnicastAddresses)
            {
                IPAddress? mask = null;
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    try
                    {
                        mask = unicast.IPv4Mask;
                    }
                    catch (NetworkInformationException)
                    {
                        // IPv4Mask is not available on every platform snapshot; treat as unknown.
                    }
                }
                addresses.Add(new IPAdapterUnicastAddress(unicast.Address, mask));
            }
            result.Add(new IPAdapterUnicastInfo(network.Id, addresses));
        }
        return result;
    }

    public IPAddress? SelectLocalAddress(string adapterId, AddressFamilyKind family, IPAddress clientAddress)
    {
        ArgumentNullException.ThrowIfNull(adapterId);
        ArgumentNullException.ThrowIfNull(clientAddress);
        // Hot-path contract: indexed access over IReadOnlyList<T> keeps the per-SYN lookup free
        // of interface-enumerator boxing.
        var snapshot = GetSnapshot();
        var adapters = snapshot.Adapters;
        for (var adapterIndex = 0; adapterIndex < adapters.Count; adapterIndex++)
        {
            var adapter = adapters[adapterIndex];
            if (!MatchesAdapter(adapter.Id, adapterId)) continue;
            IPAddress? fallback = null;
            var addresses = adapter.Addresses;
            for (var addressIndex = 0; addressIndex < addresses.Count; addressIndex++)
            {
                var candidate = addresses[addressIndex];
                if (!IsCandidate(candidate.Address, family)) continue;
                fallback ??= candidate.Address;
                if (SharesClientSubnet(candidate, clientAddress)) return candidate.Address;
            }
            return fallback;
        }
        return null;
    }

    /// <summary>
    /// Returns the current address snapshot, rebuilding it at most once per invalidation even
    /// when several pump threads race: the fast path is a lock-free volatile read, and callers
    /// that lose the rebuild gate re-check freshness before enumerating again.
    /// </summary>
    private AddressSnapshot GetSnapshot()
    {
        var snapshot = Volatile.Read(ref _snapshot);
        if (snapshot is not null && IsFresh(snapshot)) return snapshot;

        lock (_rebuildGate)
        {
            snapshot = Volatile.Read(ref _snapshot);
            if (snapshot is not null && IsFresh(snapshot)) return snapshot;

            IReadOnlyList<IPAdapterUnicastInfo> adapters;
            try
            {
                adapters = _adapters();
            }
            catch when (snapshot is not null)
            {
                // Enumeration failure keeps the last snapshot: stale beats fail-closed for
                // address selection, and the new capture stamp bounds retries to one per TTL.
                adapters = snapshot.Adapters;
            }

            snapshot = new AddressSnapshot(adapters, _timeProvider.GetUtcNow(), invalidated: false);
            Volatile.Write(ref _snapshot, snapshot);
            return snapshot;
        }
    }

    private bool IsFresh(AddressSnapshot snapshot)
        => !snapshot.Invalidated && _timeProvider.GetUtcNow() - snapshot.CapturedAtUtc < SnapshotSafetyTimeToLive;

    private void InvalidateSnapshot()
    {
        // Coalesced change events only mark the snapshot stale; the next SelectLocalAddress
        // rebuilds it lazily so an event burst never enumerates on the notification thread.
        while (true)
        {
            var snapshot = Volatile.Read(ref _snapshot);
            if (snapshot is null || snapshot.Invalidated) return;
            var stale = snapshot.MarkInvalidated();
            if (ReferenceEquals(Interlocked.CompareExchange(ref _snapshot, stale, snapshot), snapshot)) return;
        }
    }

    private static bool MatchesAdapter(string candidateId, string wantedId)
    {
        if (WindowsAdapterInventory.TryExtractGuid(candidateId, out var candidateGuid) &&
            WindowsAdapterInventory.TryExtractGuid(wantedId, out var wantedGuid))
        {
            return string.Equals(candidateGuid, wantedGuid, StringComparison.OrdinalIgnoreCase);
        }
        return string.Equals(candidateId, wantedId, StringComparison.Ordinal);
    }

    private static bool IsCandidate(IPAddress address, AddressFamilyKind family) => family switch
    {
        AddressFamilyKind.IPv4 => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address),
        AddressFamilyKind.IPv6 => address.AddressFamily == AddressFamily.InterNetworkV6 && !IPAddress.IsLoopback(address) && !IsIPv6LinkLocal(address),
        _ => false,
    };

    private static bool SharesClientSubnet(IPAdapterUnicastAddress candidate, IPAddress clientAddress)
    {
        var address = candidate.Address;
        if (address.AddressFamily != clientAddress.AddressFamily) return false;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            if (candidate.Ipv4Mask is null) return false;
            var mask = candidate.Ipv4Mask.GetAddressBytes();
            if (mask.Length != 4 || mask.All(octet => octet == 0)) return false;
            var local = address.GetAddressBytes();
            var client = clientAddress.GetAddressBytes();
            for (var index = 0; index < 4; index++)
            {
                if ((local[index] & mask[index]) != (client[index] & mask[index])) return false;
            }
            return true;
        }

        var localBytes = address.GetAddressBytes();
        var clientBytes = clientAddress.GetAddressBytes();
        return localBytes.AsSpan(0, 8).SequenceEqual(clientBytes.AsSpan(0, 8));
    }

    private static bool IsIPv6LinkLocal(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 16 && bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80;
    }

    /// <summary>
    /// The immutable adapter snapshot: one <see cref="IPAdapterUnicastInfo"/> entry per adapter
    /// Id, published atomically so the pump-thread read path takes no locks and never observes
    /// torn state. Invalidation replaces the instance (with <see cref="Invalidated"/> set) rather
    /// than mutating it; a rebuild publishes a brand-new instance with a fresh capture stamp.
    /// </summary>
    private sealed class AddressSnapshot(IReadOnlyList<IPAdapterUnicastInfo> adapters, DateTimeOffset capturedAtUtc, bool invalidated)
    {
        public IReadOnlyList<IPAdapterUnicastInfo> Adapters { get; } = adapters;
        public DateTimeOffset CapturedAtUtc { get; } = capturedAtUtc;
        public bool Invalidated { get; } = invalidated;

        public AddressSnapshot MarkInvalidated() => new(Adapters, CapturedAtUtc, invalidated: true);
    }
}
