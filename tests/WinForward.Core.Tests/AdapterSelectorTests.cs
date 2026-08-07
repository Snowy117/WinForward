using System.Runtime.Versioning;
using WinForward.Windows;
using Xunit;

namespace WinForward.Core.Tests;

public sealed class AdapterSelectorTests
{
    [Fact]
    public void SelectorRequiresExactNameAndRejectsAmbiguity()
    {
        var adapters = new[]
        {
            new WindowsAdapter("id-a", "vEthernet 1", "a", 1, 1),
            new WindowsAdapter("id-b", "vEthernet 1", "b", 2, 1)
        };

        Assert.False(AdapterSelector.TryResolve(adapters, null, "vEthernet 1", out _, out var error));
        Assert.Equal("Adapter selector is ambiguous.", error);
        Assert.True(AdapterSelector.TryResolve(adapters, "id-a", "vEthernet 1", out var resolved, out _));
        Assert.Equal("id-a", resolved!.StableId);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void AdapterSnapshotUsesOneGenerationForAllAdapters()
    {
        var inventory = new WindowsAdapterInventory(() =>
        [
            ("internal-a", (nint)1, new byte[] { 1, 2, 3, 4, 5, 6 }, (ushort)1500),
            ("internal-b", (nint)2, new byte[] { 6, 5, 4, 3, 2, 1 }, (ushort)1500)
        ]);

        var first = inventory.GetCurrentAdapters();
        var second = inventory.GetCurrentAdapters();

        Assert.Equal(first[0].Generation, first[1].Generation);
        Assert.True(second[0].Generation > first[0].Generation);
        Assert.Equal(second[0].Generation, second[1].Generation);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void AdapterInventoryCorrelatesDeviceGuidToFriendlyName()
    {
        const string Guid = "{DD8CD9A1-1111-2222-3333-444455556666}";
        var inventory = new WindowsAdapterInventory(
            () => [("\\DEVICE\\" + Guid, (nint)1, new byte[] { 1, 2, 3, 4, 5, 6 }, (ushort)1500)],
            () => [new IpAdapterInfo(Guid, "Ethernet", new byte[] { 0, 0, 0, 0, 0, 0 })]);

        var adapter = Assert.Single(inventory.GetCurrentAdapters());

        Assert.Equal(Guid, adapter.StableId);
        Assert.Equal("Ethernet", adapter.FriendlyName);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void AdapterInventoryGuidCorrelationIsCaseInsensitiveAndBraceInsensitive()
    {
        const string InternalGuid = "\u007Bdd8cd9a1-1111-2222-3333-444455556666\u007D";
        const string IpHelperId = "DD8CD9A1-1111-2222-3333-444455556666";
        var inventory = new WindowsAdapterInventory(
            () => [(InternalGuid, (nint)1, new byte[] { 1, 2, 3, 4, 5, 6 }, (ushort)1500)],
            () => [new IpAdapterInfo(IpHelperId, "Ethernet", new byte[] { 0, 0, 0, 0, 0, 0 })]);

        var adapter = Assert.Single(inventory.GetCurrentAdapters());

        Assert.Equal(IpHelperId, adapter.StableId);
        Assert.Equal("Ethernet", adapter.FriendlyName);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void AdapterInventoryFallsBackToMacWhenInternalNameIsNotAGuid()
    {
        var mac = new byte[] { 1, 2, 3, 4, 5, 6 };
        var inventory = new WindowsAdapterInventory(
            () => [("Ethernet", (nint)1, mac, (ushort)1500)],
            () => [new IpAdapterInfo("some-id", "vEthernet (Default)", mac)]);

        var adapter = Assert.Single(inventory.GetCurrentAdapters());

        Assert.Equal("some-id", adapter.StableId);
        Assert.Equal("vEthernet (Default)", adapter.FriendlyName);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void AdapterInventoryRecognizesBareGuidInternalName()
    {
        const string Guid = "7C1A2B3C-4D5E-4F60-8A9B-0C1D2E3F4A5B";
        var inventory = new WindowsAdapterInventory(
            () => [(Guid, (nint)1, new byte[] { 1, 2, 3, 4, 5, 6 }, (ushort)1500)],
            () => [new IpAdapterInfo(Guid, "Ethernet 8", new byte[] { 0, 0, 0, 0, 0, 0 })]);

        var adapter = Assert.Single(inventory.GetCurrentAdapters());

        Assert.Equal(Guid, adapter.StableId);
        Assert.Equal("Ethernet 8", adapter.FriendlyName);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void AdapterInventoryFallsBackToInternalNameWithoutAnyCorrelation()
    {
        var inventory = new WindowsAdapterInventory(
            () => [("not-a-guid-and-no-mac", (nint)1, new byte[] { 1, 2, 3, 4, 5, 6 }, (ushort)1500)],
            () => [new IpAdapterInfo("other-id", "Other", new byte[] { 9, 9, 9, 9, 9, 9 })]);

        var adapter = Assert.Single(inventory.GetCurrentAdapters());

        Assert.Equal("not-a-guid-and-no-mac", adapter.StableId);
        Assert.Equal("not-a-guid-and-no-mac", adapter.FriendlyName);
    }
}
