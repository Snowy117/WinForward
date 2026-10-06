using Xunit;

namespace WinForward.Runtime.UdpProxy.Tests;

/// <summary>
/// The tests that move or measure the process-wide UDP counters whose writers overlap:
/// <c>udpLocalTargetFailures</c> and <c>udpResponseSourceMismatch</c>. xUnit parallelises across
/// collections, so every fact whose assertion is an exact or zero delta on either counter runs
/// sequentially with the others here; a class left outside could perturb those deltas by running a
/// local-target session or a source-mismatched reply of its own concurrently. The two counters share
/// one collection because a class can join only one, and <see cref="LocalUdpTransportTests"/> writes
/// both.
/// </summary>
[CollectionDefinition(Name)]
public static class UdpProcessCounterCollection
{
    /// <summary>The collection shared by every local-target and response-source counter test in this assembly.</summary>
    public const string Name = "udp-process-counters";
}
