using WinForward.Runtime.Capture;
using Xunit;

namespace WinForward.Core.Tests;

public sealed class AdapterEnumerationDiffTests
{
    [Fact]
    public void IdenticalLinkStateIsEmpty()
    {
        var current = new[] { CaptureRunnerFakes.AdapterItem("id-a", 101), CaptureRunnerFakes.AdapterItem("id-b", 202) };
        var next = new[] { CaptureRunnerFakes.AdapterItem("id-a", 101), CaptureRunnerFakes.AdapterItem("id-b", 202) };

        var diff = AdapterEnumerationDiff.Diff(current, next);

        Assert.True(diff.IsEmpty);
    }

    [Fact]
    public void StableIdComparisonIsCaseInsensitive()
    {
        var current = new[] { CaptureRunnerFakes.AdapterItem("id-a", 101) };
        var next = new[] { CaptureRunnerFakes.AdapterItem("ID-A", 101) };

        var diff = AdapterEnumerationDiff.Diff(current, next);

        Assert.True(diff.IsEmpty);
    }

    [Fact]
    public void NewAdapterIsAddedAndMissingAdapterIsRemoved()
    {
        var current = new[] { CaptureRunnerFakes.AdapterItem("id-a", 101) };
        var next = new[] { CaptureRunnerFakes.AdapterItem("id-b", 202) };

        var diff = AdapterEnumerationDiff.Diff(current, next);

        Assert.False(diff.IsEmpty);
        Assert.Equal(["id-b"], [.. diff.Added.Select(item => item.StableId)]);
        Assert.Equal(["id-a"], [.. diff.Removed.Select(item => item.StableId)]);
        Assert.Empty(diff.Changed);
    }

    [Fact]
    public void HandleChangeIsChanged()
    {
        var current = new[] { CaptureRunnerFakes.AdapterItem("id-a", 101) };
        var next = new[] { CaptureRunnerFakes.AdapterItem("id-a", 202) };

        var diff = AdapterEnumerationDiff.Diff(current, next);

        Assert.Equal(["id-a"], [.. diff.Changed.Select(item => item.StableId)]);
        Assert.Empty(diff.Added);
        Assert.Empty(diff.Removed);
    }

    [Fact]
    public void MacChangeIsChanged()
    {
        var current = new[] { CaptureRunnerFakes.AdapterItem("id-a", 101, firstMacOctet: 1) };
        var next = new[] { CaptureRunnerFakes.AdapterItem("id-a", 101, firstMacOctet: 9) };

        var diff = AdapterEnumerationDiff.Diff(current, next);

        Assert.Equal(["id-a"], [.. diff.Changed.Select(item => item.StableId)]);
    }

    [Fact]
    public void MtuChangeIsChanged()
    {
        var current = new[] { CaptureRunnerFakes.AdapterItem("id-a", 101, mtu: 1500) };
        var next = new[] { CaptureRunnerFakes.AdapterItem("id-a", 101, mtu: 9000) };

        var diff = AdapterEnumerationDiff.Diff(current, next);

        Assert.Equal(["id-a"], [.. diff.Changed.Select(item => item.StableId)]);
    }

    [Fact]
    public void AddressFingerprintChangeAloneIsChanged()
    {
        // The task 09-17 outage shape: identical handle/MAC/MTU, only the host addresses rotated
        // (IPv6 temporary-address churn) while the NDISRD bound-adapter list never rebuilt.
        var current = new[] { CaptureRunnerFakes.AdapterItem("id-a", 101, addressFingerprint: "192.168.77.2;240c:c001:101::1") };
        var next = new[] { CaptureRunnerFakes.AdapterItem("id-a", 101, addressFingerprint: "192.168.77.2;240c:c001:202::9") };

        var diff = AdapterEnumerationDiff.Diff(current, next);

        Assert.Equal(["id-a"], [.. diff.Changed.Select(item => item.StableId)]);
        Assert.Empty(diff.Added);
        Assert.Empty(diff.Removed);
    }

    [Fact]
    public void IdenticalAddressFingerprintStaysEmpty()
    {
        var current = new[] { CaptureRunnerFakes.AdapterItem("id-a", 101, addressFingerprint: "192.168.77.2;240c:c001:101::1") };
        var next = new[] { CaptureRunnerFakes.AdapterItem("id-a", 101, addressFingerprint: "192.168.77.2;240c:c001:101::1") };

        var diff = AdapterEnumerationDiff.Diff(current, next);

        Assert.True(diff.IsEmpty);
    }

    [Fact]
    public void EmptyVersusPopulatedFingerprintIsChanged()
    {
        // An address query that fails (empty fingerprint) after one that succeeded is a real
        // link-state observation transition: exactly one rebuild, then equality — never a loop.
        var current = new[] { CaptureRunnerFakes.AdapterItem("id-a", 101, addressFingerprint: "192.168.77.2") };
        var next = new[] { CaptureRunnerFakes.AdapterItem("id-a", 101, addressFingerprint: "") };

        var diff = AdapterEnumerationDiff.Diff(current, next);
        Assert.False(diff.IsEmpty);

        var settled = AdapterEnumerationDiff.Diff(next, next);
        Assert.True(settled.IsEmpty);
    }
}
