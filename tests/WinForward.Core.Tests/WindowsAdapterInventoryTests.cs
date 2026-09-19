using System.Runtime.Versioning;
using WinForward.Windows;
using Xunit;

namespace WinForward.Core.Tests;

public sealed class WindowsAdapterInventoryTests
{
    [Fact]
    [SupportedOSPlatform("windows")]
    public void AdapterSnapshotUsesOneGenerationForAllAdapters()
    {
        var inventory = new WindowsAdapterInventory(() =>
        [
            ("internal-a", (nint)1, new byte[] { 1, 2, 3, 4, 5, 6 }, (ushort)1500),
            ("internal-b", (nint)2, new byte[] { 6, 5, 4, 3, 2, 1 }, (ushort)1500),
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
        const string guid = "{DD8CD9A1-1111-2222-3333-444455556666}";
        var inventory = new WindowsAdapterInventory(
            () => [("\\DEVICE\\" + guid, (nint)1, new byte[] { 1, 2, 3, 4, 5, 6 }, (ushort)1500)],
            () => [new IPAdapterInfo(guid, "Ethernet", [0, 0, 0, 0, 0, 0])]);

        var adapter = Assert.Single(inventory.GetCurrentAdapters());

        Assert.Equal(guid, adapter.StableId);
        Assert.Equal("Ethernet", adapter.FriendlyName);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void AdapterInventoryGuidCorrelationIsCaseInsensitiveAndBraceInsensitive()
    {
        const string internalGuid = "\u007Bdd8cd9a1-1111-2222-3333-444455556666\u007D";
        const string ipHelperId = "DD8CD9A1-1111-2222-3333-444455556666";
        var inventory = new WindowsAdapterInventory(
            () => [(internalGuid, (nint)1, new byte[] { 1, 2, 3, 4, 5, 6 }, (ushort)1500)],
            () => [new IPAdapterInfo(ipHelperId, "Ethernet", [0, 0, 0, 0, 0, 0])]);

        var adapter = Assert.Single(inventory.GetCurrentAdapters());

        Assert.Equal(ipHelperId, adapter.StableId);
        Assert.Equal("Ethernet", adapter.FriendlyName);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void AdapterInventoryFallsBackToMacWhenInternalNameIsNotAGuid()
    {
        var mac = new byte[] { 1, 2, 3, 4, 5, 6 };
        var inventory = new WindowsAdapterInventory(
            () => [("Ethernet", (nint)1, mac, (ushort)1500)],
            () => [new IPAdapterInfo("some-id", "vEthernet (Default)", mac)]);

        var adapter = Assert.Single(inventory.GetCurrentAdapters());

        Assert.Equal("some-id", adapter.StableId);
        Assert.Equal("vEthernet (Default)", adapter.FriendlyName);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void AdapterInventoryRejectsZeroMacFallback()
    {
        var inventory = new WindowsAdapterInventory(
            () => [("not-a-guid", (nint)1, new byte[] { 0, 0, 0, 0, 0, 0 }, (ushort)1500)],
            () => [new IPAdapterInfo("other-id", "Other", [0, 0, 0, 0, 0, 0])]);

        var adapter = Assert.Single(inventory.GetCurrentAdapters());

        Assert.Equal("not-a-guid", adapter.StableId);
        Assert.Equal("not-a-guid", adapter.FriendlyName);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void AdapterInventoryRejectsAmbiguousMacFallback()
    {
        var mac = new byte[] { 1, 2, 3, 4, 5, 6 };
        var inventory = new WindowsAdapterInventory(
            () => [("not-a-guid", (nint)1, mac, (ushort)1500)],
            () =>
            [
                new IPAdapterInfo("first", "First", mac),
                new IPAdapterInfo("second", "Second", mac),
            ]);

        var adapter = Assert.Single(inventory.GetCurrentAdapters());

        Assert.Equal("not-a-guid", adapter.StableId);
        Assert.Equal("not-a-guid", adapter.FriendlyName);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void AdapterInventoryUsesUniqueGuidDespiteDuplicateMacs()
    {
        const string guid = "{DD8CD9A1-1111-2222-3333-444455556666}";
        var mac = new byte[] { 1, 2, 3, 4, 5, 6 };
        var inventory = new WindowsAdapterInventory(
            () => [("\\DEVICE\\" + guid, (nint)1, mac, (ushort)1500)],
            () =>
            [
                new IPAdapterInfo(guid, "Ethernet", mac),
                new IPAdapterInfo("other-id", "Other", mac),
            ]);

        var adapter = Assert.Single(inventory.GetCurrentAdapters());

        Assert.Equal(guid, adapter.StableId);
        Assert.Equal("Ethernet", adapter.FriendlyName);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void AdapterInventoryUsesUniqueMacWhenGuidCorrelationIsAmbiguous()
    {
        const string guid = "{DD8CD9A1-1111-2222-3333-444455556666}";
        var mac = new byte[] { 1, 2, 3, 4, 5, 6 };
        var inventory = new WindowsAdapterInventory(
            () => [("\\DEVICE\\" + guid, (nint)1, mac, (ushort)1500)],
            () =>
            [
                new IPAdapterInfo(guid, "First", [6, 5, 4, 3, 2, 1]),
                new IPAdapterInfo(guid, "Second", [9, 8, 7, 6, 5, 4]),
                new IPAdapterInfo("mac-match", "Fallback", mac),
            ]);

        var adapter = Assert.Single(inventory.GetCurrentAdapters());

        Assert.Equal("mac-match", adapter.StableId);
        Assert.Equal("Fallback", adapter.FriendlyName);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void AdapterInventoryRecognizesBareGuidInternalName()
    {
        const string guid = "7C1A2B3C-4D5E-4F60-8A9B-0C1D2E3F4A5B";
        var inventory = new WindowsAdapterInventory(
            () => [(guid, (nint)1, new byte[] { 1, 2, 3, 4, 5, 6 }, (ushort)1500)],
            () => [new IPAdapterInfo(guid, "Ethernet 8", [0, 0, 0, 0, 0, 0])]);

        var adapter = Assert.Single(inventory.GetCurrentAdapters());

        Assert.Equal(guid, adapter.StableId);
        Assert.Equal("Ethernet 8", adapter.FriendlyName);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void AdapterInventoryFallsBackToInternalNameWithoutAnyCorrelation()
    {
        var inventory = new WindowsAdapterInventory(
            () => [("not-a-guid-and-no-mac", (nint)1, new byte[] { 1, 2, 3, 4, 5, 6 }, (ushort)1500)],
            () => [new IPAdapterInfo("other-id", "Other", "\t\t\t\t\t\t"u8.ToArray())]);

        var adapter = Assert.Single(inventory.GetCurrentAdapters());

        Assert.Equal("not-a-guid-and-no-mac", adapter.StableId);
        Assert.Equal("not-a-guid-and-no-mac", adapter.FriendlyName);
    }
}
