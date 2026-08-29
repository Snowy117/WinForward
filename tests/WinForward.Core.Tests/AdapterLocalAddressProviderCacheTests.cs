using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using WinForward.Core;
using WinForward.Windows;
using Xunit;

namespace WinForward.Core.Tests;

/// <summary>
/// Pins the address-snapshot cache of <see cref="WindowsAdapterLocalAddressProvider"/>: steady
/// state performs zero interface enumerations, invalidation is driven by the change-event and
/// safety-TTL seams, enumeration failure serves the last snapshot, and concurrent first lookups
/// rebuild exactly once.
/// </summary>
public sealed class AdapterLocalAddressProviderCacheTests
{
    private const string AdapterGuid = "{E14A2A2E-F7E2-4428-942E-6D04D1C6D797}";

    [Fact]
    [SupportedOSPlatform("windows")]
    public void SteadyStateLookupsEnumerateOnlyOnce()
    {
        var harness = new CacheHarness(Subnet100());

        for (var lookup = 0; lookup < 10; lookup++)
        {
            Assert.Equal(IPAddress.Parse("192.168.100.1"), harness.Select());
        }

        Assert.Equal(1, harness.Enumeration.Calls);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void ChangeEventInvalidatesSnapshotAndReEnumerates()
    {
        var harness = new CacheHarness(Subnet100());
        Assert.Equal(IPAddress.Parse("192.168.100.1"), harness.Select());

        harness.Enumeration.Replace(Subnet10());
        harness.Change.Fire();

        Assert.Equal(IPAddress.Parse("10.0.0.1"), harness.Select(client: "10.0.0.5"));
        Assert.Equal(2, harness.Enumeration.Calls);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void SnapshotSurvivesWithinTtlWithoutChangeEvent()
    {
        var harness = new CacheHarness(Subnet100());
        harness.Select();

        harness.Time.Advance(TimeSpan.FromSeconds(29));
        harness.Select();

        Assert.Equal(1, harness.Enumeration.Calls);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void TtlExpiryReEnumeratesWithoutChangeEvent()
    {
        var harness = new CacheHarness(Subnet100());
        harness.Select();

        harness.Enumeration.Replace(Subnet10());
        harness.Time.Advance(TimeSpan.FromSeconds(31));

        Assert.Equal(IPAddress.Parse("10.0.0.1"), harness.Select(client: "10.0.0.5"));
        Assert.Equal(2, harness.Enumeration.Calls);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void EnumerationFailureServesLastSnapshot()
    {
        var harness = new CacheHarness(Subnet100());
        Assert.Equal(IPAddress.Parse("192.168.100.1"), harness.Select());

        harness.Enumeration.FailNext();
        harness.Change.Fire();

        Assert.Equal(IPAddress.Parse("192.168.100.1"), harness.Select());
        Assert.Equal(2, harness.Enumeration.Calls);

        harness.Select();
        Assert.Equal(2, harness.Enumeration.Calls);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void EmptySnapshotYieldsNoCandidate()
    {
        var harness = new CacheHarness([new IPAdapterUnicastInfo(AdapterGuid, [])]);

        Assert.Null(harness.Select());
        Assert.Null(harness.Select(adapterId: "{11111111-2222-3333-4444-555555555555}"));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void CoalescedChangeEventsReEnumerateAtMostOnce()
    {
        var harness = new CacheHarness(Subnet100());
        harness.Select();

        harness.Enumeration.Replace(Subnet10());
        harness.Change.Fire();
        harness.Change.Fire();
        harness.Change.Fire();

        Assert.Equal(IPAddress.Parse("10.0.0.1"), harness.Select(client: "10.0.0.5"));
        Assert.Equal(2, harness.Enumeration.Calls);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task ConcurrentFirstLookupsEnumerateExactlyOnce()
    {
        var enumeration = new CountingEnumeration(Subnet100());
        var change = new ManualAddressChange();
        var provider = new WindowsAdapterLocalAddressProvider(enumeration.Enumerate, TimeProvider.System, change.Subscribe);
        using var barrier = new Barrier(4);
        var results = new IPAddress?[4];

        var workers = Enumerable.Range(0, 4).Select(index => Task.Run(() =>
        {
            barrier.SignalAndWait();
            results[index] = provider.SelectLocalAddress(AdapterGuid, AddressFamilyKind.IPv4, IPAddress.Parse("192.168.100.6"));
        })).ToArray();
        await Task.WhenAll(workers);

        Assert.Equal(1, enumeration.Calls);
        Assert.All(results, selected => Assert.Equal(IPAddress.Parse("192.168.100.1"), selected));
    }

    private static IReadOnlyList<IPAdapterUnicastInfo> Subnet100() =>
        [new IPAdapterUnicastInfo(AdapterGuid,
        [
            new IPAdapterUnicastAddress(IPAddress.Parse("192.168.100.1"), IPAddress.Parse("255.255.255.0"))
        ])];

    private static IReadOnlyList<IPAdapterUnicastInfo> Subnet10() =>
        [new IPAdapterUnicastInfo(AdapterGuid,
        [
            new IPAdapterUnicastAddress(IPAddress.Parse("10.0.0.1"), IPAddress.Parse("255.0.0.0"))
        ])];

    /// <summary>
    /// Wires the provider to the counting enumeration, fake clock, and manual change-event seams,
    /// and offers a one-line lookup with defaults matching <see cref="Subnet100"/>.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private sealed class CacheHarness
    {
        public CacheHarness(IReadOnlyList<IPAdapterUnicastInfo> initial)
        {
            Enumeration = new CountingEnumeration(initial);
            Time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
            Change = new ManualAddressChange();
            Provider = new WindowsAdapterLocalAddressProvider(Enumeration.Enumerate, Time, Change.Subscribe);
        }

        public CountingEnumeration Enumeration { get; }
        public MutableTimeProvider Time { get; }
        public ManualAddressChange Change { get; }
        public WindowsAdapterLocalAddressProvider Provider { get; }

        public IPAddress? Select(
            string adapterId = AdapterGuid,
            AddressFamilyKind family = AddressFamilyKind.IPv4,
            string client = "192.168.100.6")
            => Provider.SelectLocalAddress(adapterId, family, IPAddress.Parse(client));
    }

    private sealed class CountingEnumeration(IReadOnlyList<IPAdapterUnicastInfo> initial)
    {
        private Func<IReadOnlyList<IPAdapterUnicastInfo>> _supplier = () => initial;

        public int Calls { get; private set; }

        public IReadOnlyList<IPAdapterUnicastInfo> Enumerate()
        {
            Calls++;
            return _supplier();
        }

        public void Replace(IReadOnlyList<IPAdapterUnicastInfo> adapters) => _supplier = () => adapters;

        public void FailNext() => _supplier = () => throw new NetworkInformationException();
    }

    private sealed class ManualAddressChange
    {
        private Action? _callback;

        public void Subscribe(Action callback) => _callback = callback;

        public void Fire() => _callback?.Invoke();
    }
}
