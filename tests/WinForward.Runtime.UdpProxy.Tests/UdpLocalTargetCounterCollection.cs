using Xunit;

namespace WinForward.Runtime.UdpProxy.Tests;

/// <summary>
/// The tests that move or measure <c>udpLocalTargetFailures</c>. The counter is process-wide and xUnit
/// parallelises across collections, so every fact whose assertion is an exact delta joins this one
/// collection and runs sequentially with the other tests that touch it; a class left outside it could
/// perturb those deltas by running its own local-target session concurrently.
/// </summary>
[CollectionDefinition(Name)]
public static class UdpLocalTargetCounterCollection
{
    /// <summary>The collection shared by every local-target failure-counter test in this assembly.</summary>
    public const string Name = "udp-local-target-failures";
}
