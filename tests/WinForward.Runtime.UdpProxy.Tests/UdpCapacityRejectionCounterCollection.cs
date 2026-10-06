using Xunit;

namespace WinForward.Runtime.UdpProxy.Tests;

/// <summary>
/// The tests that move or measure <c>udpCapacityRejections</c>. The counter is process-wide and xUnit
/// parallelises across collections, so every fact whose assertion is an exact delta joins this one
/// collection and runs sequentially with the others that touch it; a class left outside could perturb
/// the delta by driving its own coordinator to capacity concurrently.
/// </summary>
[CollectionDefinition(Name)]
public static class UdpCapacityRejectionCounterCollection
{
    /// <summary>The collection shared by every capacity-rejection counter test in this assembly.</summary>
    public const string Name = "udp-capacity-rejections";
}
