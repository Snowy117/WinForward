using System.Net;
using WinForward.Core;

namespace WinForward.Runtime.Socks5;

/// <summary>
/// A bounded host-to-address cache shared by the SOCKS5 control and UDP transports. The configured
/// server endpoint is resolved once at startup (and on failure) so steady-state connections never
/// touch DNS; reply-domain resolution is cached the same way. Cold path only: reads and writes are
/// serialized by a leaf lock and allocation is permitted.
/// </summary>
public sealed class Socks5AddressCache
{
    private const int DefaultCapacity = 64;
    private static readonly Func<string, CancellationToken, ValueTask<IPAddress[]>> s_defaultResolver =
        static (host, token) => new ValueTask<IPAddress[]>(Dns.GetHostAddressesAsync(host, token));

    private readonly Dictionary<string, IPAddressValue> _addresses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private readonly Func<string, CancellationToken, ValueTask<IPAddress[]>> _resolver;
    private readonly int _capacity;

    public Socks5AddressCache(Func<string, CancellationToken, ValueTask<IPAddress[]>>? resolver = null, int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _resolver = resolver ?? s_defaultResolver;
        _capacity = capacity;
    }

    public bool TryGet(string host, out IPAddressValue address)
    {
        lock (_gate) return _addresses.TryGetValue(host, out address);
    }

    /// <summary>Stores the first resolved address; at capacity one existing entry is dropped instead of growing.</summary>
    public void Set(string host, IPAddressValue address)
    {
        lock (_gate)
        {
            if (_addresses.Count >= _capacity && !_addresses.ContainsKey(host)) EvictOneUnderGate();
            _addresses[host] = address;
        }
    }

    public void Invalidate(string host)
    {
        lock (_gate) _addresses.Remove(host);
    }

    /// <summary>Returns the cached address when present, otherwise resolves (cold) and stores it.</summary>
    public async ValueTask<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        if (TryGet(host, out var cached)) return [cached.ToIPAddress()];
        var addresses = await _resolver(host, cancellationToken).ConfigureAwait(false);
        if (addresses.Length > 0) Set(host, IPAddressValue.From(addresses[0]));
        return addresses;
    }

    private void EvictOneUnderGate()
    {
        using var keys = _addresses.Keys.GetEnumerator();
        if (keys.MoveNext()) _addresses.Remove(keys.Current);
    }
}
