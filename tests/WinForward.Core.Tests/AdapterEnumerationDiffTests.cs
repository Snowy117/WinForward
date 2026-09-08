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
        Assert.Equal(["id-b"], diff.Added.Select(item => item.StableId).ToArray());
        Assert.Equal(["id-a"], diff.Removed.Select(item => item.StableId).ToArray());
        Assert.Empty(diff.Changed);
    }

    [Fact]
    public void HandleChangeIsChanged()
    {
        var current = new[] { CaptureRunnerFakes.AdapterItem("id-a", 101) };
        var next = new[] { CaptureRunnerFakes.AdapterItem("id-a", 202) };

        var diff = AdapterEnumerationDiff.Diff(current, next);

        Assert.Equal(["id-a"], diff.Changed.Select(item => item.StableId).ToArray());
        Assert.Empty(diff.Added);
        Assert.Empty(diff.Removed);
    }

    [Fact]
    public void MacChangeIsChanged()
    {
        var current = new[] { CaptureRunnerFakes.AdapterItem("id-a", 101, firstMacOctet: 1) };
        var next = new[] { CaptureRunnerFakes.AdapterItem("id-a", 101, firstMacOctet: 9) };

        var diff = AdapterEnumerationDiff.Diff(current, next);

        Assert.Equal(["id-a"], diff.Changed.Select(item => item.StableId).ToArray());
    }

    [Fact]
    public void MtuChangeIsChanged()
    {
        var current = new[] { CaptureRunnerFakes.AdapterItem("id-a", 101, mtu: 1500) };
        var next = new[] { CaptureRunnerFakes.AdapterItem("id-a", 101, mtu: 9000) };

        var diff = AdapterEnumerationDiff.Diff(current, next);

        Assert.Equal(["id-a"], diff.Changed.Select(item => item.StableId).ToArray());
    }
}
