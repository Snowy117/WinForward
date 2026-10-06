using Xunit;

namespace WinForward.Runtime.UdpProxy.Tests;

/// <summary>
/// The tests that move or measure <c>udpAssociationLost</c>. The counter is process-wide and xUnit
/// parallelises across collections, so every fact whose assertion is an exact delta joins this one
/// collection and runs sequentially with the other tests that touch it; a class left outside it
/// could perturb those deltas by classifying an association loss of its own concurrently.
/// </summary>
[CollectionDefinition(Name)]
public static class UdpAssociationLostCounterCollection
{
    /// <summary>The collection shared by every association-loss counter test in this assembly.</summary>
    public const string Name = "udp-association-lost";
}
